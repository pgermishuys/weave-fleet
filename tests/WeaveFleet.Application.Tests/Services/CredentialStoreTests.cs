using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CredentialStore"/>.
/// Tests verify encrypt-on-write, metadata-only listing, and that values are never returned in API responses.
/// </summary>
public sealed class CredentialStoreTests
{
    private readonly InMemoryUserCredentialRepository _repo = new();
    private readonly FakeCredentialProtector _protector = new();
    private readonly IUserContext _userContext = new TestUserContext("user-1");
    private readonly CredentialStore _sut;

    public CredentialStoreTests()
    {
        _sut = new CredentialStore(_repo, _protector, _userContext);
    }

    // ── StoreCredentialAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task StoreCredentialAsync_EncryptsValueBeforePersisting()
    {
        await _sut.StoreCredentialAsync("My Key", "anthropic", "api-key", "sk-plaintext");

        var stored = _repo.All.Single(c => c.Label == "My Key");
        stored.EncryptedValue.ShouldBe("ENC:sk-plaintext");
        stored.Namespace.ShouldBe("anthropic");
        stored.Kind.ShouldBe("api-key");
    }

    [Fact]
    public async Task StoreCredentialAsync_SetsDisplayHintToLastFourChars()
    {
        await _sut.StoreCredentialAsync("Work Key", "anthropic", "api-key", "sk-ant-api03-ABCDEFGH");

        var stored = _repo.All.Single(c => c.Label == "Work Key");
        stored.DisplayHint.ShouldBe("...EFGH");
    }

    [Fact]
    public async Task StoreCredentialAsync_SetsDisplayHintToMaskedWhenValueIsShort()
    {
        await _sut.StoreCredentialAsync("Short Key", "custom", "api-key", "abc");

        var stored = _repo.All.Single(c => c.Label == "Short Key");
        stored.DisplayHint.ShouldBe("***");
    }

    [Fact]
    public async Task StoreCredentialAsync_DoesNotPersistPlaintextValue()
    {
        await _sut.StoreCredentialAsync("Key", "openai", "api-key", "sk-openai-REAL_VALUE");

        var stored = _repo.All.Single(c => c.Label == "Key");
        stored.EncryptedValue.ShouldNotBe("sk-openai-REAL_VALUE");
        stored.EncryptedValue.ShouldBe("ENC:sk-openai-REAL_VALUE");
    }

    [Fact]
    public async Task StoreCredentialAsync_PersistsMetadataAndCurrentUser()
    {
        await _sut.StoreCredentialAsync("Work Key", "anthropic", "api-key", "sk-secret", "{\"scope\":\"work\"}");

        var stored = _repo.All.Single(c => c.Label == "Work Key");
        stored.UserId.ShouldBe("user-1");
        stored.Metadata.ShouldBe("{\"scope\":\"work\"}");
        stored.Namespace.ShouldBe("anthropic");
        stored.Kind.ShouldBe("api-key");
    }

    // ── ListCredentialsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ListCredentialsAsync_ReturnsMetadataOnly_NeverValue()
    {
        _repo.Seed(new UserCredential
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
        });

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
        _repo.Seed(
            new UserCredential { Id = "c1", UserId = "user-1", Namespace = "anthropic", Kind = "api-key",
                Label = "Key 1", EncryptedValue = "ENC:val1", DisplayHint = "...al1", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" },
            new UserCredential { Id = "c2", UserId = "user-1", Namespace = "anthropic", Kind = "api-key",
                Label = "Key 2", EncryptedValue = "ENC:val2", DisplayHint = "...al2", CreatedAt = "2026-01-02", UpdatedAt = "2026-01-02" });

        var results = await _sut.ListCredentialsAsync();

        results.Count.ShouldBe(2);
        results[0].Label.ShouldBe("Key 1");
        results[1].Label.ShouldBe("Key 2");
    }

    // ── DeleteCredentialAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCredentialAsync_DelegatesToRepository()
    {
        _repo.Seed(new UserCredential { Id = "cred-1", UserId = "user-1", Label = "Key", Namespace = "n", Kind = "k",
            EncryptedValue = "ENC:v", DisplayHint = "...", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" });

        await _sut.DeleteCredentialAsync("cred-1");

        _repo.All.ShouldBeEmpty();
    }

    // ── GetDecryptedCredentialsAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetDecryptedCredentialsAsync_DecryptsValuesBeforeReturning()
    {
        _repo.Seed(new UserCredential
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
        });

        var decrypted = await _sut.GetDecryptedCredentialsAsync("user-1");

        decrypted.Count.ShouldBe(1);
        decrypted[0].EncryptedValue.ShouldBe("plaintext-value"); // Decrypt strips "ENC:" prefix
        decrypted[0].Namespace.ShouldBe("anthropic");
        decrypted[0].Kind.ShouldBe("api-key");
    }

    [Fact]
    public async Task GetDecryptedCredentialsAsync_ReturnsEmptyListWhenNoCredentials()
    {
        var decrypted = await _sut.GetDecryptedCredentialsAsync("user-1");

        decrypted.ShouldBeEmpty();
    }
}
