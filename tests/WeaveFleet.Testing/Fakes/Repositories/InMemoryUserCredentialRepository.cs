using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryUserCredentialRepository : IUserCredentialRepository
{
    private readonly Dictionary<string, UserCredential> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(UserCredential credential) => _store[credential.Id] = credential;

    public void Seed(params UserCredential[] credentials)
    {
        foreach (var c in credentials)
            Seed(c);
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<UserCredential> All => [.. _store.Values];

    // ── IUserCredentialRepository ────────────────────────────────────────────

    public Task<UserCredential?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<UserCredential?> GetByIdAsync(string id, string userId)
        => Task.FromResult(_store.Values.FirstOrDefault(c => c.Id == id && c.UserId == userId));

    public Task<IReadOnlyList<UserCredential>> ListByUserAsync()
    {
        // No user context here — return all (tests should seed only relevant data)
        IReadOnlyList<UserCredential> result = [.. _store.Values];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserCredential>> ListByUserAsync(string userId)
    {
        IReadOnlyList<UserCredential> result = [.. _store.Values.Where(c => c.UserId == userId)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string credentialNamespace)
    {
        IReadOnlyList<UserCredential> result = [.. _store.Values.Where(c => c.Namespace == credentialNamespace)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string userId, string credentialNamespace)
    {
        IReadOnlyList<UserCredential> result = [.. _store.Values.Where(c => c.UserId == userId && c.Namespace == credentialNamespace)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string credentialNamespace, string kind)
    {
        IReadOnlyList<UserCredential> result = [.. _store.Values.Where(c => c.Namespace == credentialNamespace && c.Kind == kind)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string userId, string credentialNamespace, string kind)
    {
        IReadOnlyList<UserCredential> result = [.. _store.Values.Where(c => c.UserId == userId && c.Namespace == credentialNamespace && c.Kind == kind)];
        return Task.FromResult(result);
    }

    public Task UpsertAsync(UserCredential credential)
    {
        // Upsert by (UserId, Label) — remove existing with same label for same user
        var existing = _store.Values.FirstOrDefault(c => c.UserId == credential.UserId && c.Label == credential.Label);
        if (existing is not null && existing.Id != credential.Id)
            _store.Remove(existing.Id);
        _store[credential.Id] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, string userId)
    {
        if (_store.TryGetValue(id, out var credential) && credential.UserId == userId)
            _store.Remove(id);
        return Task.CompletedTask;
    }
}
