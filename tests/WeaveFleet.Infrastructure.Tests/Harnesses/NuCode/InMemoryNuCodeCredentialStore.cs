using NuCode.Providers;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.NuCode;

/// <summary>
/// In-memory implementation of <see cref="INuCodeCredentialStore"/> for use in tests.
/// </summary>
internal sealed class InMemoryNuCodeCredentialStore : INuCodeCredentialStore
{
    private readonly Dictionary<(string ProviderId, string FieldKey), StoredCredential> _store =
        new(EqualityComparer<(string, string)>.Create(
            (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
            x => HashCode.Combine(
                x.Item1.ToLowerInvariant().GetHashCode(),
                x.Item2.ToLowerInvariant().GetHashCode())));

    public Task<StoredCredential?> GetAsync(string providerId, string fieldKey, CancellationToken ct = default)
    {
        _store.TryGetValue((providerId, fieldKey), out var result);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StoredCredential>> GetAllForProviderAsync(string providerId, CancellationToken ct = default)
    {
        IReadOnlyList<StoredCredential> results = _store
            .Where(kv => string.Equals(kv.Key.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToList();
        return Task.FromResult(results);
    }

    public Task SetAsync(string providerId, string fieldKey, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        _store[(providerId, fieldKey)] = new StoredCredential(providerId, fieldKey, value, expiresAt);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, string fieldKey, CancellationToken ct = default)
    {
        _store.Remove((providerId, fieldKey));
        return Task.CompletedTask;
    }

    public Task DeleteAllForProviderAsync(string providerId, CancellationToken ct = default)
    {
        var keys = _store.Keys
            .Where(k => string.Equals(k.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
            _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListConfiguredProviderIdsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> ids = _store.Keys
            .Select(k => k.ProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(ids);
    }
}
