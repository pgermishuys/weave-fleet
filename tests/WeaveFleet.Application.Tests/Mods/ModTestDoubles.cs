using System.Text.Json;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>An in-memory <see cref="IModVersionStore"/> that follows the interface's documentation, for service tests.</summary>
internal sealed class InMemoryModVersionStore : IModVersionStore
{
    private readonly Dictionary<(string User, string Name), ModHistory> _histories = [];
    private readonly Dictionary<(string User, string Session, string Name), ModDraft> _drafts = [];
    private readonly Dictionary<string, ModManifest> _manifests = [];
    private readonly Dictionary<string, List<ModFile>> _files = [];

    /// <summary>Calls that changed something, in order, so a test can see that a refused operation wrote nothing.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The manifest and files <see cref="VersionFolder"/> shows for a kept version.</summary>
    public void SeedVersion(string user, string name, int number, string version = "0.1.0", string description = "Shows chips")
    {
        var folder = VersionFolder(user, name, number);
        _manifests[folder] = new ModManifest(name, version, description, "mod.ts");
        _files[folder] = [new ModFile("mod.json", "{}"), new ModFile("mod.ts", $"// v{number}")];
    }

    public void SeedHistory(string user, ModHistory history) => _histories[(user, history.Name)] = history;

    public void SeedDraft(string user, string session, string name, ModOff? off = null, bool withManifest = true)
    {
        var draft = new ModDraft(session, name, DraftFolder(user, session, name), off);
        _drafts[(user, session, name)] = draft;
        if (withManifest)
            _manifests[draft.Folder] = new ModManifest(name, "0.1.0", "Shows chips", "mod.ts");
        _files[draft.Folder] = [new ModFile("mod.json", "{}"), new ModFile("mod.ts", "// draft")];
    }

    public Task<IReadOnlyList<ModHistory>> ListAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModHistory>>(_histories.Where(h => h.Key.User == userId).Select(h => h.Value).OrderBy(h => h.Name, StringComparer.Ordinal).ToList());

    public Task<ModHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => Task.FromResult(_histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name));

    public Task<ModVersion> KeepAsync(string userId, string sessionId, string name, ModKeepSource source, CancellationToken ct = default)
    {
        if (!_drafts.TryGetValue((userId, sessionId, name), out var draft) || !_manifests.TryGetValue(draft.Folder, out var manifest))
            throw new ModStoreException($"There is no draft of {name} to keep.");

        var history = _histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name);
        var number = history.Versions.Count == 0 ? 1 : history.Versions.Max(v => v.Number) + 1;
        var version = new ModVersion(number, new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), manifest.Version, "sha", sessionId, source.SessionTitle, source.Note, source.Check);
        SeedVersion(userId, name, number, manifest.Version, manifest.Description);
        _histories[(userId, name)] = history with { Active = number, Off = null, Versions = [.. history.Versions, version] };
        _drafts.Remove((userId, sessionId, name));
        Writes.Add($"keep {name} v{number}");
        return Task.FromResult(version);
    }

    public Task SetActiveAsync(string userId, string name, int number, CancellationToken ct = default)
    {
        var history = _histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name);
        if (history.Versions.All(v => v.Number != number))
            throw new ModStoreException($"{name} has no version {number}.");
        _histories[(userId, name)] = history with { Active = number };
        Writes.Add($"active {name} {number}");
        return Task.CompletedTask;
    }

    public Task SetOffAsync(string userId, string name, ModOff? off, CancellationToken ct = default)
    {
        var history = _histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name);
        _histories[(userId, name)] = history with { Off = off };
        Writes.Add($"off {name} {off?.By ?? "on"}");
        return Task.CompletedTask;
    }

    public string VersionFolder(string userId, string name, int number) => $"/mods/{userId}/{name}/v{number}";

    public Task<ModManifest?> ReadManifestAsync(string folder, CancellationToken ct = default)
        => Task.FromResult(_manifests.GetValueOrDefault(folder));

    public Task<IReadOnlyList<ModFile>?> ReadVersionFilesAsync(string userId, string name, int number, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModFile>?>(_files.GetValueOrDefault(VersionFolder(userId, name, number)));

    public Task<IReadOnlyList<ModDraft>> ListDraftsAsync(string userId, string sessionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModDraft>>(_drafts.Where(d => d.Key.User == userId && d.Key.Session == sessionId).Select(d => d.Value).OrderBy(d => d.Name, StringComparer.Ordinal).ToList());

    public Task<ModDraft?> GetDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => Task.FromResult(_drafts.GetValueOrDefault((userId, sessionId, name)));

    public string DraftFolder(string userId, string sessionId, string name) => $"/mods/{userId}/drafts/{sessionId}/{name}";

    public Task SetDraftOffAsync(string userId, string sessionId, string name, ModOff? off, CancellationToken ct = default)
    {
        if (_drafts.TryGetValue((userId, sessionId, name), out var draft))
            _drafts[(userId, sessionId, name)] = draft with { Off = off };
        Writes.Add($"draft-off {sessionId}/{name} {off?.By ?? "on"}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModFile>?> ReadDraftFilesAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModFile>?>(_drafts.ContainsKey((userId, sessionId, name)) ? _files.GetValueOrDefault(DraftFolder(userId, sessionId, name)) : null);

    public Task<JsonElement?> GetValueAsync(string userId, string name, string key, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
    public Task SetValueAsync(string userId, string name, string key, JsonElement value, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteValueAsync(string userId, string name, string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> KeysAsync(string userId, string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Answers with a fixed report and remembers which folders it was asked about.</summary>
internal sealed class FakeModChecker(JsonElement? report = null) : IModChecker
{
    public JsonElement? Report { get; set; } = report;
    public List<string> Checked { get; } = [];

    public Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default)
    {
        Checked.Add(folder);
        return Task.FromResult(Report);
    }
}
