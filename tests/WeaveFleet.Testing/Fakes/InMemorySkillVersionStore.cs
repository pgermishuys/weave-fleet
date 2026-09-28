using WeaveFleet.Application.Skills;

namespace WeaveFleet.Testing.Fakes;

/// <summary>The user's versions of built-in skills, kept in memory. Folders are made-up paths nothing reads.</summary>
public sealed class InMemorySkillVersionStore : ISkillVersionStore
{
    private readonly Dictionary<(string User, string Name), SkillVersionHistory> _histories = [];
    private readonly Dictionary<(string User, string Name, int Number), string> _texts = [];
    private readonly Dictionary<string, string> _fleet = [];

    public Task<SkillVersionHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => Task.FromResult(_histories.GetValueOrDefault((userId, name)) ?? SkillVersionHistory.Empty(name));

    public Task<IReadOnlyList<SkillVersionHistory>> ListAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SkillVersionHistory>>(
            _histories.Where(entry => entry.Key.User == userId).Select(entry => entry.Value).OrderBy(h => h.Name, StringComparer.Ordinal).ToList());

    public async Task<SkillVersion> AddAsync(
        string userId, string name, string content, SkillVersionSource source, string fleetContent, CancellationToken ct = default)
    {
        var history = await GetAsync(userId, name, ct);
        var hash = SkillVersions.Hash(fleetContent);
        _fleet[hash] = fleetContent;
        var version = new SkillVersion(history.Versions.Count + 1, DateTimeOffset.UtcNow, source.Note, source.SessionId, source.SessionTitle, hash);
        _texts[(userId, name, version.Number)] = content;
        _histories[(userId, name)] = history with { Active = version.Number, KeptFleetHash = null, Versions = [.. history.Versions, version] };
        return version;
    }

    public async Task SetActiveAsync(string userId, string name, int? number, CancellationToken ct = default)
        => _histories[(userId, name)] = (await GetAsync(userId, name, ct)) with { Active = number };

    public async Task KeepOverAsync(string userId, string name, string fleetContent, CancellationToken ct = default)
    {
        var hash = SkillVersions.Hash(fleetContent);
        _fleet[hash] = fleetContent;
        _histories[(userId, name)] = (await GetAsync(userId, name, ct)) with { KeptFleetHash = hash };
    }

    public Task<string?> ReadAsync(string userId, string name, int number, CancellationToken ct = default)
        => Task.FromResult(_texts.GetValueOrDefault((userId, name, number)));

    public Task<string?> ReadFleetAsync(string userId, string name, string fleetHash, CancellationToken ct = default)
        => Task.FromResult(_fleet.GetValueOrDefault(fleetHash));

    public string FolderFor(string userId, string name, int number) => $"/versions/{userId}/{name}/v{number}";
}
