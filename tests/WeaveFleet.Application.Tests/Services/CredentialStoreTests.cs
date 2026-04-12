using NSubstitute;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CredentialStore"/>.
/// Tests verify encrypt-on-write, metadata-only listing, and that values are never returned in API responses.
/// </summary>
public sealed class CredentialStoreTests
{
    private readonly IUserCredentialRepository _repo = Substitute.For<IUserCredentialRepository>();
    private readonly ICredentialProtector _protector = Substitute.For<ICredentialProtector>();
    private readonly IUserContext _userContext = new StubUserContext("user-1");
    private readonly CredentialStore _sut;

    public CredentialStoreTests()
    {
        _sut = new CredentialStore(_repo, _protector, _userContext);

        // Default stub: encrypt/decrypt are identity transforms with a prefix for tracing
        _protector.Encrypt(Arg.Any<string>()).Returns(ci => $"ENC:{ci.Arg<string>()}");
        _protector.Decrypt(Arg.Any<string>()).Returns(ci =>
        {
            var v = ci.Arg<string>();
            return v.StartsWith("ENC:", StringComparison.Ordinal) ? v[4..] : v;
        });
    }

    // ── StoreCredentialAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task StoreCredentialAsync_EncryptsValueBeforePersisting()
    {
        await _sut.StoreCredentialAsync("My Key", "anthropic", "api-key", "sk-plaintext");

        await _repo.Received(1).UpsertAsync(Arg.Is<UserCredential>(c =>
            c.EncryptedValue == "ENC:sk-plaintext" &&
            c.Label == "My Key" &&
            c.Namespace == "anthropic" &&
            c.Kind == "api-key"));
    }

    [Fact]
    public async Task StoreCredentialAsync_SetsDisplayHintToLastFourChars()
    {
        await _sut.StoreCredentialAsync("Work Key", "anthropic", "api-key", "sk-ant-api03-ABCDEFGH");

        await _repo.Received(1).UpsertAsync(Arg.Is<UserCredential>(c =>
            c.DisplayHint == "...EFGH"));
    }

    [Fact]
    public async Task StoreCredentialAsync_SetsDisplayHintToMaskedWhenValueIsShort()
    {
        await _sut.StoreCredentialAsync("Short Key", "custom", "api-key", "abc");

        await _repo.Received(1).UpsertAsync(Arg.Is<UserCredential>(c =>
            c.DisplayHint == "***"));
    }

    [Fact]
    public async Task StoreCredentialAsync_DoesNotPersistPlaintextValue()
    {
        await _sut.StoreCredentialAsync("Key", "openai", "api-key", "sk-openai-REAL_VALUE");

        await _repo.Received(1).UpsertAsync(Arg.Is<UserCredential>(c =>
            c.EncryptedValue != "sk-openai-REAL_VALUE"));

        _protector.Received(1).Encrypt("sk-openai-REAL_VALUE");
    }

    [Fact]
    public async Task StoreCredentialAsync_PersistsMetadataAndCurrentUser()
    {
        await _sut.StoreCredentialAsync("Work Key", "anthropic", "api-key", "sk-secret", "{\"scope\":\"work\"}");

        await _repo.Received(1).UpsertAsync(Arg.Is<UserCredential>(c =>
            c.UserId == "user-1" &&
            c.Metadata == "{\"scope\":\"work\"}" &&
            c.Namespace == "anthropic" &&
            c.Kind == "api-key"));
    }

    // ── ListCredentialsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ListCredentialsAsync_ReturnsMetadataOnly_NeverValue()
    {
        _repo.ListByUserAsync().Returns([
            new UserCredential
            {
                Id = "cred-1",
                UserId = "user-1",
                Namespace = "anthropic",
                Kind = "api-key",
                Label = "My Key",
                EncryptedValue = "ENC:supersecret",
                DisplayHint = "...cret",
                CreatedAt = "2026-01-01",
                UpdatedAt = "2026-01-01"
            }
        ]);

        var results = await _sut.ListCredentialsAsync();

        results.Count.ShouldBe(1);
        var summary = results[0];
        summary.Id.ShouldBe("cred-1");
        summary.Label.ShouldBe("My Key");
        summary.Namespace.ShouldBe("anthropic");
        summary.Kind.ShouldBe("api-key");
        summary.DisplayHint.ShouldBe("...cret");

        // The CredentialSummary record type does not have a Value/EncryptedValue field —
        // verify by checking that no property would expose the plaintext.
        // (This is enforced by the type system — CredentialSummary has no Value property.)
        typeof(CredentialSummary).GetProperty("Value").ShouldBeNull();
        typeof(CredentialSummary).GetProperty("EncryptedValue").ShouldBeNull();
    }

    [Fact]
    public async Task ListCredentialsAsync_SupportsMultipleCredentialsSameNamespaceAndKind()
    {
        _repo.ListByUserAsync().Returns([
            new UserCredential { Id = "c1", UserId = "user-1", Namespace = "anthropic", Kind = "api-key",
                Label = "Key 1", EncryptedValue = "ENC:val1", DisplayHint = "...al1", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" },
            new UserCredential { Id = "c2", UserId = "user-1", Namespace = "anthropic", Kind = "api-key",
                Label = "Key 2", EncryptedValue = "ENC:val2", DisplayHint = "...al2", CreatedAt = "2026-01-02", UpdatedAt = "2026-01-02" }
        ]);

        var results = await _sut.ListCredentialsAsync();

        results.Count.ShouldBe(2);
        results[0].Label.ShouldBe("Key 1");
        results[1].Label.ShouldBe("Key 2");
    }

    // ── DeleteCredentialAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCredentialAsync_DelegatesToRepository()
    {
        await _sut.DeleteCredentialAsync("cred-1");

        await _repo.Received(1).DeleteAsync("cred-1");
    }

    // ── GetDecryptedCredentialsAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetDecryptedCredentialsAsync_DecryptsValuesBeforeReturning()
    {
        _repo.ListByUserAsync("user-1").Returns([
            new UserCredential
            {
                Id = "cred-1",
                UserId = "user-1",
                Namespace = "anthropic",
                Kind = "api-key",
                Label = "My Key",
                EncryptedValue = "ENC:plaintext-value",
                DisplayHint = "...alue",
                CreatedAt = "2026-01-01",
                UpdatedAt = "2026-01-01"
            }
        ]);

        var decrypted = await _sut.GetDecryptedCredentialsAsync("user-1");

        decrypted.Count.ShouldBe(1);
        decrypted[0].EncryptedValue.ShouldBe("plaintext-value"); // Decrypt strips "ENC:" prefix
        decrypted[0].Namespace.ShouldBe("anthropic");
        decrypted[0].Kind.ShouldBe("api-key");

        _protector.Received(1).Decrypt("ENC:plaintext-value");
    }

    [Fact]
    public async Task GetDecryptedCredentialsAsync_ReturnsEmptyListWhenNoCredentials()
    {
        _repo.ListByUserAsync("user-1").Returns([]);

        var decrypted = await _sut.GetDecryptedCredentialsAsync("user-1");

        decrypted.ShouldBeEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private sealed class StubUserContext(string userId) : IUserContext
    {
        public string UserId => userId;
        public string? Email => null;
        public string? DisplayName => null;
        public bool IsAuthenticated => true;
    }
}
