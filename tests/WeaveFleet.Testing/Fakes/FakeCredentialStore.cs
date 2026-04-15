using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly List<UserCredential> _credentials = [];

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(UserCredential credential) => _credentials.Add(credential);

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<UserCredential> All => [.. _credentials];

    // ── Call tracking ────────────────────────────────────────────────────────

    public List<string> GetDecryptedCredentialsCalls { get; } = [];

    /// <summary>
    /// Optional override for <see cref="GetDecryptedCredentialsAsync"/>. When set, called instead of filtering the in-memory store.
    /// Use this when tests need to assert on the exact list reference passed to downstream services.
    /// </summary>
    public Func<string, Task<IReadOnlyList<UserCredential>>>? GetDecryptedCredentialsBehavior { get; set; }

    // ── ICredentialStore ─────────────────────────────────────────────────────

    public Task<IReadOnlyList<CredentialSummary>> ListCredentialsAsync()
    {
        IReadOnlyList<CredentialSummary> result = [.. _credentials.Select(c => new CredentialSummary(
            c.Id,
            c.Label,
            c.Namespace,
            c.Kind,
            c.DisplayHint,
            c.Metadata,
            c.CreatedAt,
            c.UpdatedAt))];
        return Task.FromResult(result);
    }

    public Task StoreCredentialAsync(string label, string credentialNamespace, string kind, string value, string? metadata = null)
    {
        var existing = _credentials.FirstOrDefault(c => c.Label == label);
        if (existing is not null)
        {
            existing.EncryptedValue = value;
            existing.Metadata = metadata;
            existing.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }
        else
        {
            _credentials.Add(new UserCredential
            {
                Id = Guid.NewGuid().ToString(),
                Label = label,
                Namespace = credentialNamespace,
                Kind = kind,
                EncryptedValue = value,
                DisplayHint = value.Length >= 4 ? value[^4..] : value,
                Metadata = metadata,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        }
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(string id)
    {
        var credential = _credentials.FirstOrDefault(c => c.Id == id);
        if (credential is not null)
            _credentials.Remove(credential);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserCredential>> GetDecryptedCredentialsAsync(string userId)
    {
        GetDecryptedCredentialsCalls.Add(userId);
        if (GetDecryptedCredentialsBehavior is not null)
            return GetDecryptedCredentialsBehavior(userId);
        IReadOnlyList<UserCredential> result = [.. _credentials.Where(c => c.UserId == userId || string.IsNullOrEmpty(c.UserId))];
        return Task.FromResult(result);
    }
}
