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

    // The mod host reads the store from its own tasks while a test writes to it: what the host reads is locked.
    private readonly Lock _sync = new();

    /// <summary>Calls that changed something, in order, so a test can see that a refused operation wrote nothing.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The staged copies <see cref="KeepAsync"/> handed to the check callback, in order.</summary>
    public List<string> Staged { get; } = [];

    /// <summary>The manifest and files <see cref="VersionFolder"/> shows for a kept version.</summary>
    public void SeedVersion(string user, string name, int number, string version = "0.1.0", string description = "Shows chips")
    {
        var folder = VersionFolder(user, name, number);
        _manifests[folder] = new ModManifest(name, version, description, "mod.ts");
        _files[folder] = [new ModFile("mod.json", "{}"), new ModFile("mod.ts", $"// v{number}")];
    }

    public void SeedHistory(string user, ModHistory history)
    {
        lock (_sync)
            _histories[(user, history.Name)] = history;
    }

    public void SeedDraft(string user, string session, string name, ModOff? off = null, bool withManifest = true)
    {
        var manifest = withManifest ? new ModManifest(name, "0.1.0", "Shows chips", "mod.ts") : null;
        var draft = new ModDraft(session, name, DraftFolder(user, session, name), off, manifest);
        lock (_sync)
        {
            _drafts[(user, session, name)] = draft;
            _files[draft.Folder] = [new ModFile("mod.json", "{}"), new ModFile("mod.ts", "// draft")];
        }
    }

    /// <summary>Deletes the session's draft folder, as the agent might.</summary>
    public void RemoveDraft(string user, string session, string name)
    {
        lock (_sync)
            _drafts.Remove((user, session, name));
    }

    public Task<IReadOnlyList<string>> ListDraftSessionsAsync(string userId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<string>>(_drafts.Keys.Where(k => k.User == userId).Select(k => k.Session).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    public string DraftsRoot(string userId) => $"/mods/{userId}/drafts";

    /// <summary>
    /// A real folder to put <see cref="HostFolder"/> under, for tests that look at the staged copies on disk; null keeps
    /// the made-up path, and staging writes nothing.
    /// </summary>
    public string? HostRoot { get; init; }

    public string HostFolder(string userId) => HostRoot is { } root ? Path.Combine(root, userId, ".host") : $"/mods/{userId}/.host";

    /// <summary>The drafts <see cref="StageDraftAsync"/> copied, as (session, name, destination), in order.</summary>
    public List<(string Session, string Name, string Destination)> StagedDrafts { get; } = [];

    /// <summary>Makes <see cref="StageDraftAsync"/> refuse with this message, as the real store does for a draft it can't copy.</summary>
    public string? StageRefusal { get; set; }

    public Task StageDraftAsync(string userId, string sessionId, string name, string destination, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_drafts.ContainsKey((userId, sessionId, name)))
                throw new ModStoreException($"There is no draft of {name}.");
            if (StageRefusal is { } refusal)
                throw new ModStoreException(refusal);
            if (HostRoot is not null)
            {
                if (Directory.Exists(destination))
                    throw new ModStoreException($"{destination} already exists.");
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "mod.json"), "{}");
            }
            StagedDrafts.Add((sessionId, name, destination));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModHistory>> ListAsync(string userId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ModHistory>>(_histories.Where(h => h.Key.User == userId).Select(h => h.Value).OrderBy(h => h.Name, StringComparer.Ordinal).ToList());
    }

    public Task<ModHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => Task.FromResult(_histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name));

    /// <summary>Where Keep stages the copy it checks: never the draft's own folder.</summary>
    public static string StagedFolder(string userId, string sessionId, string name) => $"/mods/{userId}/{name}/keep.staging-{sessionId}.tmp/{name}";

    public async Task<ModVersion> KeepAsync(string userId, string sessionId, string name, ModKeepSource source, ModKeepCheck check, CancellationToken ct = default)
    {
        if (!_drafts.TryGetValue((userId, sessionId, name), out var draft) || draft.Manifest is not { } manifest)
            throw new ModStoreException($"There is no draft of {name} to keep.");

        var staged = StagedFolder(userId, sessionId, name);
        Staged.Add(staged);
        var report = await check(staged, ct);
        if (ModChecks.Refusal(report) is { } refusal)
            throw new ModStoreException(refusal);

        var history = _histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name);
        var number = history.Versions.Count == 0 ? 1 : history.Versions.Max(v => v.Number) + 1;
        var version = new ModVersion(number, new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), manifest.Version, "sha", sessionId, source.SessionTitle, source.Note, report);
        SeedVersion(userId, name, number, manifest.Version, manifest.Description);
        _histories[(userId, name)] = history with { Active = number, Off = null, Versions = [.. history.Versions, version] };
        _drafts.Remove((userId, sessionId, name));
        Writes.Add($"keep {name} v{number}");
        return version;
    }

    public Task<ModHistory> UseVersionAsync(string userId, string name, int number, CancellationToken ct = default)
    {
        var history = Existing(userId, name);
        if (history.Versions.All(v => v.Number != number))
            throw new ModStoreException($"{name} has no version {number}.");

        var next = history with { Active = number, Off = null };
        lock (_sync)
            _histories[(userId, name)] = next;
        Writes.Add($"use {name} {number}");
        return Task.FromResult(next);
    }

    public Task<ModHistory> UndoAsync(string userId, string name, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_sync)
            return Undo(userId, name, at);
    }

    private Task<ModHistory> Undo(string userId, string name, DateTimeOffset at)
    {
        var history = Existing(userId, name);
        var active = history.Active ?? throw new ModStoreException($"There is no mod called {name}.");
        var previous = history.Versions.Where(v => v.Number < active).OrderByDescending(v => v.Number).FirstOrDefault();

        ModHistory next;
        if (previous is not null)
            next = history with { Active = previous.Number, Off = null };
        else if (history.Off is not null)
            throw new ModStoreException("Nothing to undo");
        else
            next = history with { Off = new ModOff(ModOffBy.User, at) };

        _histories[(userId, name)] = next;
        Writes.Add($"undo {name}");
        return Task.FromResult(next);
    }

    public Task<ModHistory> SetOffAsync(string userId, string name, ModOff? off, CancellationToken ct = default)
    {
        ModHistory next;
        lock (_sync)
        {
            next = (_histories.GetValueOrDefault((userId, name)) ?? ModHistory.Empty(name)) with { Off = off };
            _histories[(userId, name)] = next;
            Writes.Add($"off {name} {off?.By ?? "on"}");
        }
        return Task.FromResult(next);
    }

    private ModHistory Existing(string userId, string name)
        => _histories.GetValueOrDefault((userId, name)) is { Versions.Count: > 0 } history
            ? history
            : throw new ModStoreException($"There is no mod called {name}.");

    public string VersionFolder(string userId, string name, int number) => $"/mods/{userId}/{name}/v{number}";

    public Task<ModManifest?> ReadVersionManifestAsync(string userId, string name, int number, CancellationToken ct = default)
        => Task.FromResult(_histories.GetValueOrDefault((userId, name))?.Versions.Any(v => v.Number == number) == true
            ? _manifests.GetValueOrDefault(VersionFolder(userId, name, number))
            : null);

    public Task<IReadOnlyList<ModFile>?> ReadVersionFilesAsync(string userId, string name, int number, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModFile>?>(_files.GetValueOrDefault(VersionFolder(userId, name, number)));

    public Task<IReadOnlyList<ModDraft>> ListDraftsAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ModDraft>>(_drafts.Where(d => d.Key.User == userId && d.Key.Session == sessionId).Select(d => d.Value).OrderBy(d => d.Name, StringComparer.Ordinal).ToList());
    }

    public Task<ModDraft?> GetDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => Task.FromResult(_drafts.GetValueOrDefault((userId, sessionId, name)));

    public string DraftFolder(string userId, string sessionId, string name) => $"/mods/{userId}/drafts/{sessionId}/{name}";

    public Task SetDraftOffAsync(string userId, string sessionId, string name, ModOff? off, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_drafts.TryGetValue((userId, sessionId, name), out var draft))
                _drafts[(userId, sessionId, name)] = draft with { Off = off };
            Writes.Add($"draft-off {sessionId}/{name} {off?.By ?? "on"}");
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModFile>?> ReadDraftFilesAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModFile>?>(_drafts.ContainsKey((userId, sessionId, name)) ? _files.GetValueOrDefault(DraftFolder(userId, sessionId, name)) : null);

    public async Task<JsonElement?> CheckDraftAsync(string userId, string sessionId, string name, ModKeepCheck check, CancellationToken ct = default)
    {
        if (!_drafts.ContainsKey((userId, sessionId, name)))
            throw new ModStoreException($"There is no draft of {name} to check.");
        var staged = StagedFolder(userId, sessionId, name);
        Staged.Add(staged);
        return await check(staged, ct);
    }

    // ── $.store ─────────────────────────────────────────────────────────

    private readonly Dictionary<(string User, string Name, string Key), JsonElement> _values = [];

    /// <summary>Makes every <see cref="SetValueAsync"/> refuse, as a full store does.</summary>
    public bool StoreFull { get; set; }

    public Task<JsonElement?> GetValueAsync(string userId, string name, string key, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<JsonElement?>(_values.TryGetValue((userId, name, key), out var value) ? value : null);
    }

    public Task SetValueAsync(string userId, string name, string key, JsonElement value, CancellationToken ct = default)
    {
        if (!ModNames.IsValidStoreKey(key))
            throw new ArgumentException($"\"{key}\" isn't a store key.", nameof(key));
        if (StoreFull)
            throw new ModStoreFullException($"The store of {name} is full.");
        lock (_sync)
            _values[(userId, name, key)] = value.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(string userId, string name, string key, CancellationToken ct = default)
    {
        lock (_sync)
            _values.Remove((userId, name, key));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> KeysAsync(string userId, string name, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(k => k.User == userId && k.Name == name).Select(k => k.Key).Order(StringComparer.Ordinal).ToList());
    }
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
