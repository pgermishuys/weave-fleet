using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>Profiles in memory. Open-session counts come from the session repository it's given, if any.</summary>
public sealed class InMemoryHarnessProfileRepository(InMemorySessionRepository? sessions = null) : IHarnessProfileRepository
{
    private readonly Dictionary<string, HarnessProfile> _store = new(StringComparer.Ordinal);

    public void Seed(HarnessProfile profile) => _store[profile.Id] = profile;

    public IReadOnlyList<HarnessProfile> All => [.. _store.Values];

    public Task<IReadOnlyList<HarnessProfile>> ListAsync(string harnessType) =>
        Task.FromResult<IReadOnlyList<HarnessProfile>>(_store.Values.Where(p => p.HarnessType == harnessType).ToList());

    public Task<HarnessProfile?> GetByIdAsync(string id) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<HarnessProfile?> GetDefaultAsync(string harnessType) =>
        Task.FromResult(_store.Values.FirstOrDefault(p => p.HarnessType == harnessType && p.IsDefault));

    public Task InsertAsync(HarnessProfile profile)
    {
        _store[profile.Id] = profile;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(HarnessProfile profile)
    {
        if (!_store.ContainsKey(profile.Id))
            return Task.FromResult(false);
        _store[profile.Id] = profile;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string id) => Task.FromResult(_store.Remove(id));

    public Task SetDefaultAsync(string harnessType, string? id)
    {
        foreach (var profile in _store.Values.Where(p => p.HarnessType == harnessType))
            profile.IsDefault = profile.Id == id;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, int>> CountOpenSessionsAsync(string harnessType)
    {
        IReadOnlyDictionary<string, int> counts = (sessions?.All ?? [])
            .Where(s => s.HarnessType == harnessType && s.HarnessProfileId is not null && s.ParentSessionId is null
                && s.RetentionStatus != "archived")
            .GroupBy(s => s.HarnessProfileId!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Task.FromResult(counts);
    }
}
