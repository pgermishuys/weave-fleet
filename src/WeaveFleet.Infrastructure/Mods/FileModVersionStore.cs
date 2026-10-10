using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Infrastructure.Mods;

/// <summary>
/// Keeps each user's mods under <c>{data}/mods/{user16}/</c>:
/// <list type="bullet">
/// <item><c>{name}/versions.json</c>: the versions, which one sessions load, and whether the mod is off.</item>
/// <item><c>{name}/v{n}/</c>: a copy of the draft when it was kept, read-only, never changed once written.</item>
/// <item><c>{name}/store.json</c>: the mod's <c>$.store</c>, shared by its drafts and its kept versions.</item>
/// <item><c>drafts/{session}/{name}/</c>: what the agent is writing, and <c>off.json</c> beside them for the drafts that are off.</item>
/// </list>
/// Locking is per user and mod, so a slow call on one mod never holds up another user's or another mod's: one lock
/// (<c>{user16}/{name}</c>) covers a mod's index, versions and store, and one (<c>{user16}/drafts/{session}</c>) a session's
/// drafts and <c>off.json</c>. Keep takes the mod's lock, then the session's, always in that order. The static check runs
/// on a staged copy with no lock held: Keep stages, lets go, checks, then takes both locks again and commits only if the
/// draft is still what was checked.
/// Nothing from <c>{user16}</c> down is ever followed through a link, and every file under a draft or a version is read
/// through <see cref="SafeFile"/>, which opens it without being able to wait (<c>O_NONBLOCK</c>, <c>O_NOFOLLOW</c>) and
/// checks that the open handle is a regular file of a size that fits before reading: a named pipe, a device, or a file
/// that is too big or can't be read is refused with a reason (Keep) or skipped (Show code).
/// </summary>
public sealed class FileModVersionStore(string root) : IModVersionStore, IDisposable
{
    private const string IndexFile = "versions.json";
    private const string StoreFile = "store.json";
    private const string ManifestFile = "mod.json";
    private const string DraftsFolder = "drafts";
    private const string OffFile = "off.json";
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxIndexBytes = 16 * 1024 * 1024;
    private const int MaxOffBytes = 1024 * 1024;
    private static readonly TimeSpan StagingGrace = TimeSpan.FromHours(1);

    private static readonly string[] ModuleExtensions = [".js", ".mjs", ".ts", ".mts"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    // The store is written without escaping beyond JSON's own, so its size is UTF-8 bytes of JSON, not \uXXXX sequences.
    private static readonly ModStoreJsonContext StoreJson = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileModVersionStore(FleetOptions options)
        : this(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory, "mods"))
    {
    }

    public void Dispose()
    {
        foreach (var held in _locks.Values)
            held.Dispose();
        _locks.Clear();
    }

    public Task<ModHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => LockedAsync(ModLock(userId, name), () => ReadIndex(userId, name), ct);

    public async Task<IReadOnlyList<ModHistory>> ListAsync(string userId, CancellationToken ct = default)
    {
        var folder = UserFolder(userId);
        if (!IsClean(folder) || !Directory.Exists(folder))
            return [];

        var names = Directory.EnumerateDirectories(folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(ModNames.IsValid)
            .Order(StringComparer.Ordinal)
            .ToList();
        var histories = new List<ModHistory>();
        foreach (var name in names)
        {
            var history = await GetAsync(userId, name, ct).ConfigureAwait(false);
            if (history.Versions.Count > 0)
                histories.Add(history);
        }

        return histories;
    }

    public async Task<ModVersion> KeepAsync(string userId, string sessionId, string name, ModKeepSource source, ModKeepCheck check, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        ThrowIfInvalidSession(sessionId);
        ArgumentNullException.ThrowIfNull(check);

        var draft = DraftFolder(userId, sessionId, name);
        var modFolder = ModFolder(userId, name);
        var staged = Path.Combine(modFolder, $"keep.{Guid.NewGuid():N}.tmp");
        var moved = false;
        try
        {
            var (manifest, sha256, stagedTree) = await LockedBothAsync(userId, sessionId, name, () =>
            {
                RequireDraft(draft, modFolder, name);
                Directory.CreateDirectory(modFolder);
                DeleteLeftoverStaging(modFolder);
                CopyDraft(draft, staged);
                var (copied, hash) = ValidateCopy(staged, name);
                return (copied, hash, TreeHash(staged));
            }, ct).ConfigureAwait(false);

            // No lock is held while the check runs: it can take the mod host seconds.
            var report = await check(staged, ct).ConfigureAwait(false);
            if (ModChecks.Refusal(report) is { } refusal)
                throw new ModStoreException(refusal);

            return await LockedBothAsync(userId, sessionId, name, () =>
            {
                if (!IsClean(draft) || !IsClean(modFolder) || !Directory.Exists(draft) || TreeHash(staged) != stagedTree || !DraftIsStill(draft, stagedTree))
                    throw new ModStoreException("The draft changed while it was being checked; check it again.");

                var history = ReadIndex(userId, name);
                var number = NextNumber(modFolder, history);
                var kept = Path.Combine(modFolder, $"v{number}");
                Directory.Move(staged, kept);
                moved = true;

                var version = new ModVersion(
                    number,
                    DateTimeOffset.UtcNow,
                    manifest.Version,
                    sha256,
                    sessionId,
                    Trimmed(source.SessionTitle),
                    Trimmed(source.Note),
                    report);
                try
                {
                    WriteIndex(userId, new ModHistory(name, number, null, [.. history.Versions, version]));
                }
                catch
                {
                    TryDelete(kept);
                    throw;
                }

                MakeReadOnly(kept);
                RemoveDraft(userId, sessionId, name, draft);
                return version;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!moved)
                TryDelete(staged);
        }
    }

    public async Task<JsonElement?> CheckDraftAsync(string userId, string sessionId, string name, ModKeepCheck check, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        ThrowIfInvalidSession(sessionId);
        ArgumentNullException.ThrowIfNull(check);

        var draft = DraftFolder(userId, sessionId, name);
        var modFolder = ModFolder(userId, name);
        var staged = Path.Combine(modFolder, $"check.{Guid.NewGuid():N}.tmp");
        try
        {
            await LockedAsync(SessionLock(userId, sessionId), () =>
            {
                RequireDraft(draft, modFolder, name);
                Directory.CreateDirectory(modFolder);
                CopyDraft(draft, staged);
                return true;
            }, ct).ConfigureAwait(false);

            return await check(staged, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    private void RequireDraft(string draft, string modFolder, string name)
    {
        if (!IsClean(draft) || !IsClean(modFolder))
            throw new ModStoreException("The draft or the mod's folder is, or sits behind, a symbolic link, which can't be kept.");
        if (!Directory.Exists(draft))
            throw new ModStoreException($"There's no draft of {name} in this session.");
    }

    /// <summary>True when the live draft still has the tree that was checked; a draft that can no longer be walked has changed.</summary>
    private static bool DraftIsStill(string draft, string checkedTree)
    {
        try
        {
            return TreeHash(draft) == checkedTree;
        }
        catch (ModStoreException)
        {
            return false;
        }
    }

    /// <summary>Takes the mod's lock, then the session's, and runs <paramref name="action"/> under both.</summary>
    private async Task<T> LockedBothAsync<T>(string userId, string sessionId, string name, Func<T> action, CancellationToken ct)
    {
        var modLock = Lock(ModLock(userId, name));
        await modLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LockedAsync(SessionLock(userId, sessionId), action, ct).ConfigureAwait(false);
        }
        finally
        {
            modLock.Release();
        }
    }

    public Task<ModHistory> UseVersionAsync(string userId, string name, int number, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(ModLock(userId, name), () =>
        {
            var history = ReadIndex(userId, name);
            if (history.Versions.All(v => v.Number != number))
                throw new ModStoreException($"{name} has no version {number}.");
            return WriteIndex(userId, history with { Active = number, Off = null });
        }, ct);
    }

    public Task<ModHistory> UndoAsync(string userId, string name, DateTimeOffset at, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(ModLock(userId, name), () =>
        {
            var history = ReadIndex(userId, name);
            if (history.Active is not { } active)
                throw new ModStoreException($"{name} hasn't been kept yet.");

            var previous = history.Versions.Where(v => v.Number < active).Select(v => (int?)v.Number).Max();
            if (previous is { } number)
                return WriteIndex(userId, history with { Active = number, Off = null });
            if (history.Off is not null)
                throw new ModStoreException("Nothing to undo");
            return WriteIndex(userId, history with { Off = new ModOff(ModOffBy.User, at) });
        }, ct);
    }

    public Task<ModHistory> SetOffAsync(string userId, string name, ModOff? off, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(ModLock(userId, name), () =>
        {
            var history = ReadIndex(userId, name);
            if (history.Versions.Count == 0)
                throw new ModStoreException($"{name} hasn't been kept yet.");
            return WriteIndex(userId, history with { Off = off });
        }, ct);
    }

    public string VersionFolder(string userId, string name, int number)
    {
        ThrowIfInvalid(name);
        return Path.Combine(ModFolder(userId, name), $"v{number}");
    }

    public Task<ModManifest?> ReadVersionManifestAsync(string userId, string name, int number, CancellationToken ct = default)
        => LockedAsync(ModLock(userId, name), () =>
        {
            var folder = ListedVersionFolder(userId, name, number);
            return folder is null ? null : ParseManifest(folder);
        }, ct);

    public Task<IReadOnlyList<ModFile>?> ReadVersionFilesAsync(string userId, string name, int number, CancellationToken ct = default)
        => LockedAsync<IReadOnlyList<ModFile>?>(ModLock(userId, name), () =>
        {
            var folder = ListedVersionFolder(userId, name, number);
            return folder is null ? null : ReadFiles(folder);
        }, ct);

    /// <summary>The folder of a version the index lists, or null (not listed, or behind a link).</summary>
    private string? ListedVersionFolder(string userId, string name, int number)
    {
        if (!ModNames.IsValid(name) || ReadIndex(userId, name).Versions.All(v => v.Number != number))
            return null;
        var folder = VersionFolder(userId, name, number);
        return IsClean(folder) && Directory.Exists(folder) ? folder : null;
    }

    // Drafts

    public Task<IReadOnlyList<ModDraft>> ListDraftsAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        if (!ModNames.IsValidSessionId(sessionId))
            return Task.FromResult<IReadOnlyList<ModDraft>>([]);

        return LockedAsync(SessionLock(userId, sessionId), () =>
        {
            var session = SessionDrafts(userId, sessionId);
            if (!IsClean(session) || !Directory.Exists(session))
                return (IReadOnlyList<ModDraft>)[];

            var off = ReadDraftOff(userId, sessionId);
            return Directory.EnumerateDirectories(session)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(ModNames.IsValid)
                .Order(StringComparer.Ordinal)
                .Select(draftName => (Name: draftName, Folder: Path.Combine(session, draftName)))
                .Where(draft => IsClean(draft.Folder))
                .Select(draft => new ModDraft(sessionId, draft.Name, draft.Folder, off.GetValueOrDefault(draft.Name), ParseManifest(draft.Folder)))
                .ToList();
        }, ct);
    }

    public Task<ModDraft?> GetDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default)
    {
        if (!ModNames.IsValid(name) || !ModNames.IsValidSessionId(sessionId))
            return Task.FromResult<ModDraft?>(null);

        return LockedAsync(SessionLock(userId, sessionId), () =>
        {
            var folder = DraftFolder(userId, sessionId, name);
            return IsClean(folder) && Directory.Exists(folder)
                ? new ModDraft(sessionId, name, folder, ReadDraftOff(userId, sessionId).GetValueOrDefault(name), ParseManifest(folder))
                : null;
        }, ct);
    }

    public string DraftFolder(string userId, string sessionId, string name)
    {
        ThrowIfInvalid(name);
        ThrowIfInvalidSession(sessionId);
        return Path.Combine(SessionDrafts(userId, sessionId), name);
    }

    public Task SetDraftOffAsync(string userId, string sessionId, string name, ModOff? off, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        ThrowIfInvalidSession(sessionId);
        return LockedAsync(SessionLock(userId, sessionId), () =>
        {
            var folder = DraftFolder(userId, sessionId, name);
            if (off is not null && !(IsClean(folder) && Directory.Exists(folder)))
                throw new ModStoreException($"There's no draft of {name} in this session.");
            WriteDraftOff(userId, sessionId, name, off);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<ModFile>?> ReadDraftFilesAsync(string userId, string sessionId, string name, CancellationToken ct = default)
    {
        if (!ModNames.IsValid(name) || !ModNames.IsValidSessionId(sessionId))
            return Task.FromResult<IReadOnlyList<ModFile>?>(null);

        return LockedAsync<IReadOnlyList<ModFile>?>(SessionLock(userId, sessionId), () => ReadFiles(DraftFolder(userId, sessionId, name)), ct);
    }

    // $.store

    public Task<IReadOnlyList<string>> ListDraftSessionsAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();

    public string DraftsRoot(string userId) => Path.Combine(UserFolder(userId), DraftsFolder);

    public string HostFolder(string userId) => Path.Combine(UserFolder(userId), ".host");

    public Task StageDraftAsync(string userId, string sessionId, string name, string destination, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<JsonElement?> GetValueAsync(string userId, string name, string key, CancellationToken ct = default)
        => LockedAsync(ModLock(userId, name), () =>
            ModNames.IsValid(name) && ReadStore(userId, name, out _).TryGetValue(key, out var value) ? (JsonElement?)value : null, ct);

    public Task SetValueAsync(string userId, string name, string key, JsonElement value, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        if (!ModNames.IsValidStoreKey(key))
            throw new ArgumentException("A store key is 1 to 64 letters, digits, '_', '-' or '.'.", nameof(key));
        return LockedAsync(ModLock(userId, name), () =>
        {
            var store = ReadStore(userId, name, out var broken);
            store[key] = value;
            var json = JsonSerializer.SerializeToUtf8Bytes(store, StoreJson.DictionaryStringJsonElement);
            if (json.Length > ModStoreLimits.StoreBytes)
                throw new ModStoreFullException(
                    $"{name}'s store would hold {(json.Length / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} MiB of JSON; a mod's store holds 4 MiB at most.");
            WriteStore(userId, name, json, broken);
            return true;
        }, ct);
    }

    public Task DeleteValueAsync(string userId, string name, string key, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(ModLock(userId, name), () =>
        {
            var store = ReadStore(userId, name, out var broken);
            if (store.Remove(key))
                WriteStore(userId, name, JsonSerializer.SerializeToUtf8Bytes(store, StoreJson.DictionaryStringJsonElement), broken);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<string>> KeysAsync(string userId, string name, CancellationToken ct = default)
        => LockedAsync(ModLock(userId, name), () => ModNames.IsValid(name)
            ? (IReadOnlyList<string>)ReadStore(userId, name, out _).Keys.Order(StringComparer.Ordinal).ToList()
            : [], ct);

    // Index

    private ModHistory ReadIndex(string userId, string name)
    {
        if (!ModNames.IsValid(name))
            return ModHistory.Empty(name);

        var path = Path.Combine(ModFolder(userId, name), IndexFile);
        if (!IsClean(path) || !File.Exists(path))
            return ModHistory.Empty(name);

        ModIndex? file = null;
        if (TryRead(path, MaxIndexBytes, out var bytes))
        {
            try
            {
                file = JsonSerializer.Deserialize(bytes, ModIndexJsonContext.Default.ModIndex);
            }
            catch (JsonException)
            {
            }
        }

        if (file?.Versions is null)
            return RebuildIndex(userId, name, path, [], null, null);

        var versions = file.Versions
            .Where(v => v is { Number: > 0, Version: not null, Sha256: not null })
            .GroupBy(v => v.Number)
            .Select(group => group.First())
            .OrderBy(v => v.Number)
            .ToList();
        var listed = versions.Select(v => v.Number).ToHashSet();
        var droppedAnEntry = versions.Count < file.Versions.Count;
        var activeIsUnknown = file.Active is { } wanted && !listed.Contains(wanted);
        if (droppedAnEntry || activeIsUnknown || VersionsOnDisk(ModFolder(userId, name), listed).Count > 0)
            return RebuildIndex(userId, name, path, versions, file.Off, file.Active);

        var active = file.Active is { } number ? number : versions.Count == 0 ? (int?)null : versions[^1].Number;
        return new ModHistory(name, active, file.Off, versions);
    }

    /// <summary>
    /// A <c>versions.json</c> that can't be used (not JSON, or JSON that lists fewer versions than the <c>v{n}</c> folders
    /// hold, drops an entry, or names an active version it doesn't list) is moved aside and rebuilt: the history is never
    /// overwritten. The entries that were fine stay as they were; the rest are rebuilt from their folders, each of which
    /// holds its <c>mod.json</c>, so what the index alone knew (session, note, check) is gone from those, but not from the
    /// file that was moved aside.
    /// </summary>
    private ModHistory RebuildIndex(string userId, string name, string path, List<ModVersion> valid, ModOff? off, int? active)
    {
        File.Move(path, $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);

        var versions = valid.Concat(VersionsOnDisk(ModFolder(userId, name), valid.Select(v => v.Number).ToHashSet())).OrderBy(v => v.Number).ToList();
        if (versions.Count == 0)
            return ModHistory.Empty(name);

        var current = active is { } wanted && versions.Any(v => v.Number == wanted) ? wanted : versions[^1].Number;
        return WriteIndex(userId, new ModHistory(name, current, off, versions));
    }

    /// <summary>
    /// The versions whose <c>v{n}</c> folders hold a mod (a manifest and its hooks file) and are not in
    /// <paramref name="listed"/>. A staging folder (<c>v{n}.{guid}.tmp</c>) is not a version.
    /// </summary>
    private List<ModVersion> VersionsOnDisk(string modFolder, HashSet<int> listed)
    {
        var found = new List<ModVersion>();
        if (!IsClean(modFolder) || !Directory.Exists(modFolder))
            return found;

        foreach (var folder in Directory.EnumerateDirectories(modFolder))
        {
            if (!TryVersionNumber(Path.GetFileName(folder), out var number) || listed.Contains(number) || !IsClean(folder))
                continue;
            if (ParseManifest(folder) is not { } manifest
                || ModuleIn(folder, manifest.Hooks) is not { } module
                || SafeFile.Hash(module, ModStoreLimits.KeptBytes, out var sha256, out _) != FileProblem.None)
                continue;

            var info = new DirectoryInfo(folder);
            var created = info.CreationTimeUtc.Year > 2000 ? info.CreationTimeUtc : info.LastWriteTimeUtc;
            found.Add(new ModVersion(number, new DateTimeOffset(created, TimeSpan.Zero), manifest.Version, sha256, null, null, null, null));
        }

        return found;
    }

    private ModHistory WriteIndex(string userId, ModHistory history)
    {
        var path = Path.Combine(ModFolder(userId, history.Name), IndexFile);
        RequireClean(path);
        var file = new ModIndex { Name = history.Name, Active = history.Active, Off = history.Off, Versions = [.. history.Versions] };
        WriteAtomically(path, JsonSerializer.Serialize(file, ModIndexJsonContext.Default.ModIndex));
        return history;
    }

    private static int NextNumber(string modFolder, ModHistory history)
    {
        var highest = history.Versions.Count == 0 ? 0 : history.Versions.Max(v => v.Number);
        if (Directory.Exists(modFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(modFolder))
            {
                if (TryVersionNumber(Path.GetFileName(folder), out var n))
                    highest = Math.Max(highest, n);
            }
        }

        return highest + 1;
    }

    private static bool TryVersionNumber(string folderName, out int number)
    {
        number = 0;
        return folderName.Length > 1 && folderName[0] == 'v'
            && int.TryParse(folderName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    // Keep

    private const string UnsupportedMessage = "Fleet can't tell what a mod's files are on this system, so it can't read them safely.";

    /// <summary>
    /// What a crash between the copy and the move leaves: <c>*.tmp</c> staging folders. Only those older than an hour go:
    /// a newer one may be another Keep's or check's copy, still in use.
    /// </summary>
    private static void DeleteLeftoverStaging(string modFolder)
    {
        foreach (var folder in Directory.EnumerateDirectories(modFolder, "*.tmp"))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > StagingGrace)
                TryDelete(folder);
        }
    }

    /// <summary>The file's bytes when it is a regular file within the cap; false when it isn't or can't be read.</summary>
    private static bool TryRead(string path, long maxBytes, out byte[] data)
    {
        var problem = SafeFile.ReadAll(path, maxBytes, out data);
        return problem == FileProblem.None
            ? true
            : problem == FileProblem.Unsupported ? throw new ModStoreException(UnsupportedMessage) : false;
    }

    private readonly record struct TreeEntry(string Relative, string FullPath, bool IsDirectory);

    /// <summary>
    /// A draft's folders and files, parents first and each folder's entries in ordinal order, with paths relative to it using
    /// <c>/</c>. A link anywhere is refused, and so is a tree with more than <see cref="ModStoreLimits.KeptFiles"/> entries
    /// (the walk stops there). Nothing is followed or opened.
    /// </summary>
    private static IEnumerable<TreeEntry> WalkTree(string folder)
    {
        var count = 0;
        return Walk(new DirectoryInfo(folder), "");

        IEnumerable<TreeEntry> Walk(DirectoryInfo directory, string prefix)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = $"{prefix}{entry.Name}";
                if (IsLink(entry.FullName))
                    throw new ModStoreException($"The draft contains a symbolic link ('{relative}'), which can't be kept.");
                if (++count > ModStoreLimits.KeptFiles)
                    throw new ModStoreException($"The draft has more than {ModStoreLimits.KeptFiles} files; a mod keeps at most {ModStoreLimits.KeptFiles}.");

                if (entry is DirectoryInfo child)
                {
                    yield return new TreeEntry(relative, child.FullName, true);
                    foreach (var inner in Walk(child, $"{relative}/"))
                        yield return inner;
                }
                else
                {
                    yield return new TreeEntry(relative, entry.FullName, false);
                }
            }
        }
    }

    /// <summary>The refusal for a file of the draft that <see cref="SafeFile"/> wouldn't open, naming the file and why.</summary>
    private static ModStoreException Refused(FileProblem problem, string relative) => problem switch
    {
        FileProblem.Missing => new($"The draft's file '{relative}' went away while it was being read."),
        FileProblem.Link => new($"The draft contains a symbolic link ('{relative}'), which can't be kept."),
        FileProblem.NotRegular => new($"The draft's file '{relative}' isn't a regular file (a folder, a named pipe or a device), so it can't be kept."),
        FileProblem.Unreadable => new($"The draft's file '{relative}' can't be read."),
        FileProblem.TooLarge => new($"The draft's file '{relative}' is too large; a mod keeps at most 16 MiB in all."),
        _ => new(UnsupportedMessage),
    };

    /// <summary>
    /// Copies the draft to <paramref name="target"/> by walking it: links are refused, every file goes through
    /// <see cref="SafeFile"/> (a named pipe, a device, an unreadable or too-large file is refused by name), and the copy
    /// stops as soon as the file count or the bytes pass their limit.
    /// </summary>
    private static void CopyDraft(string draft, string target)
    {
        long copied = 0;
        Directory.CreateDirectory(target);
        foreach (var entry in WalkTree(draft))
        {
            var destination = Path.Combine(target, entry.Relative);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var problem = SafeFile.Open(entry.FullPath, ModStoreLimits.KeptBytes - copied, out var input);
            if (problem != FileProblem.None)
                throw Refused(problem, entry.Relative);

            using (input)
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input!.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > ModStoreLimits.KeptBytes)
                        throw Refused(FileProblem.TooLarge, entry.Relative);
                    output.Write(buffer, 0, read);
                }
            }
        }
    }

    /// <summary>
    /// One hash of a whole tree: its folders and files in order, each file with its SHA-256. Two trees with the same hash
    /// hold the same paths and bytes. Throws <see cref="ModStoreException"/> as <see cref="CopyDraft"/> does.
    /// </summary>
    private static string TreeHash(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var entry in WalkTree(folder))
        {
            var line = entry.IsDirectory ? $"d {entry.Relative}\n" : null;
            if (!entry.IsDirectory)
            {
                var problem = SafeFile.Hash(entry.FullPath, ModStoreLimits.KeptBytes - total, out var sha256, out var length);
                if (problem != FileProblem.None)
                    throw Refused(problem, entry.Relative);
                total += length;
                line = $"f {entry.Relative}\0{sha256}\n";
            }

            hash.AppendData(Encoding.UTF8.GetBytes(line!));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Reads and checks the manifest of the staged copy, and returns it with the SHA-256 of its hooks module.</summary>
    private static (ModManifest Manifest, string Sha256) ValidateCopy(string staged, string name)
    {
        if (!File.Exists(Path.Combine(staged, ManifestFile)))
            throw new ModStoreException("The draft has no mod.json.");
        var manifest = ParseManifest(staged)
            ?? throw new ModStoreException("mod.json isn't valid JSON with a name, version, description and hooks.");
        if (manifest.Name != name)
            throw new ModStoreException($"mod.json names the mod '{manifest.Name}', but the draft's folder is '{name}'.");

        var hooks = manifest.Hooks;
        if (Path.IsPathRooted(hooks) || hooks.StartsWith('/') || hooks.StartsWith('\\'))
            throw new ModStoreException("mod.json's hooks must be a path inside the mod's folder, not an absolute one.");

        string module;
        try
        {
            var full = Path.GetFullPath(Path.Combine(staged, hooks));
            var prefix = Path.GetFullPath(staged).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
                throw new ModStoreException("mod.json's hooks points outside the mod's folder.");
            module = full;
        }
        catch (ArgumentException)
        {
            throw new ModStoreException("mod.json's hooks isn't a valid path.");
        }

        if (!ModuleExtensions.Contains(Path.GetExtension(module), StringComparer.OrdinalIgnoreCase))
            throw new ModStoreException("mod.json's hooks must be a .js, .mjs, .ts or .mts file.");

        var problem = SafeFile.Hash(module, ModStoreLimits.KeptBytes, out var sha256, out _);
        if (problem == FileProblem.Missing)
            throw new ModStoreException($"mod.json's hooks file '{hooks}' isn't in the draft.");
        if (problem != FileProblem.None)
            throw Refused(problem, hooks);

        return (manifest, sha256);
    }

    /// <summary>The hooks module of a kept folder when its path is inside the folder; null otherwise.</summary>
    private static string? ModuleIn(string folder, string hooks)
    {
        try
        {
            if (Path.IsPathRooted(hooks))
                return null;
            var full = Path.GetFullPath(Path.Combine(folder, hooks));
            var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Makes a kept version's files read-only where the OS allows it. Folders stay traversable.</summary>
    private static void MakeReadOnly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(file, UnixFileMode.UserRead);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The version stands; it just isn't locked against edits.
        }
    }

    /// <summary>
    /// After the index lists the version: removes the draft, its <c>off.json</c> entry and the session's folder when it's
    /// empty. Best effort: the version is kept whether or not this works.
    /// </summary>
    private void RemoveDraft(string userId, string sessionId, string name, string draft)
    {
        try
        {
            if (IsClean(draft) && Directory.Exists(draft))
                Directory.Delete(draft, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            WriteDraftOff(userId, sessionId, name, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ModStoreException)
        {
        }

        try
        {
            var session = SessionDrafts(userId, sessionId);
            if (IsClean(session) && Directory.Exists(session) && !Directory.EnumerateFileSystemEntries(session).Any())
                Directory.Delete(session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ModManifest? ParseManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestFile);
        try
        {
            if (!File.Exists(path) || !TryRead(path, MaxManifestBytes, out var bytes))
                return null;

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var root = document.RootElement;
            var name = Field(root, "name");
            var version = Field(root, "version");
            var description = Field(root, "description");
            var hooks = Field(root, "hooks");
            return name is null || version is null || description is null || hooks is null ? null : new ModManifest(name, version, description, hooks);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string? Field(JsonElement root, string field)
            => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    }

    // Files

    /// <summary>
    /// The text files of a folder, at most <see cref="ModStoreLimits.ShownFiles"/> and <see cref="ModStoreLimits.ShownBytes"/>.
    /// Links, anything but regular files, files that are too big and files that aren't UTF-8 are skipped; null when the
    /// folder isn't there or is, or sits behind, a link.
    /// </summary>
    private List<ModFile>? ReadFiles(string folder)
    {
        if (!IsClean(folder) || !Directory.Exists(folder))
            return null;

        var files = new List<ModFile>();
        long total = 0;
        Walk(new DirectoryInfo(folder), "");
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        bool Walk(DirectoryInfo directory, string prefix)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (IsLink(entry.FullName))
                    continue;
                if (entry is DirectoryInfo child)
                {
                    if (!Walk(child, $"{prefix}{child.Name}/"))
                        return false;
                    continue;
                }

                // Not a regular file, too large, unreadable or gone: Show code skips it and goes on.
                if (!TryRead(entry.FullName, ModStoreLimits.ShownFileBytes, out var data))
                    continue;

                string text;
                try
                {
                    text = StrictUtf8.GetString(data);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                var bytes = Encoding.UTF8.GetByteCount(text);
                if (files.Count >= ModStoreLimits.ShownFiles || total + bytes > ModStoreLimits.ShownBytes)
                    return false;
                total += bytes;
                files.Add(new ModFile($"{prefix}{entry.Name}", text));
            }

            return true;
        }
    }

    // Draft off state

    private Dictionary<string, ModOff> ReadDraftOff(string userId, string sessionId)
    {
        var path = Path.Combine(SessionDrafts(userId, sessionId), OffFile);
        if (!IsClean(path) || !File.Exists(path) || !TryRead(path, MaxOffBytes, out var bytes))
            return [];

        try
        {
            return JsonSerializer.Deserialize(bytes, ModIndexJsonContext.Default.DictionaryStringModOff) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteDraftOff(string userId, string sessionId, string name, ModOff? off)
    {
        var path = Path.Combine(SessionDrafts(userId, sessionId), OffFile);
        RequireClean(path);
        var entries = ReadDraftOff(userId, sessionId);
        if (off is null && !entries.Remove(name))
            return;
        if (off is not null)
            entries[name] = off;

        if (entries.Count == 0)
            File.Delete(path);
        else
            WriteAtomically(path, JsonSerializer.Serialize(entries, ModIndexJsonContext.Default.DictionaryStringModOff));
    }

    // Store

    private Dictionary<string, JsonElement> ReadStore(string userId, string name, out bool broken)
    {
        broken = false;
        var path = Path.Combine(ModFolder(userId, name), StoreFile);
        if (!IsClean(path) || !File.Exists(path))
            return [];

        if (TryRead(path, 2L * ModStoreLimits.StoreBytes, out var bytes))
        {
            try
            {
                var store = JsonSerializer.Deserialize(bytes, StoreJson.DictionaryStringJsonElement);
                if (store is not null)
                    return store;
            }
            catch (JsonException)
            {
            }
        }

        broken = true;
        return [];
    }

    private void WriteStore(string userId, string name, byte[] json, bool broken)
    {
        var path = Path.Combine(ModFolder(userId, name), StoreFile);
        RequireClean(path);
        if (broken && File.Exists(path))
            File.Move(path, $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    // Plumbing

    private SemaphoreSlim Lock(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private static string ModLock(string userId, string name) => $"{UserKey(userId)}/{name}";

    private static string SessionLock(string userId, string sessionId) => $"{UserKey(userId)}/{DraftsFolder}/{sessionId}";

    private async Task<T> LockedAsync<T>(string key, Func<T> action, CancellationToken ct)
    {
        var held = Lock(key);
        await held.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            held.Release();
        }
    }

    private async Task LockedAsync(string key, Func<bool> action, CancellationToken ct) => await LockedAsync<bool>(key, action, ct).ConfigureAwait(false);

    private static string UserKey(string userId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16];

    private string UserFolder(string userId) => Path.Combine(root, UserKey(userId));

    private string ModFolder(string userId, string name) => Path.Combine(UserFolder(userId), name);

    private string SessionDrafts(string userId, string sessionId) => Path.Combine(UserFolder(userId), DraftsFolder, sessionId);

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    // Links and special files

    /// <summary>
    /// True when <paramref name="path"/> is inside the store and no segment of it from <c>{user16}</c> down exists as a link
    /// (a symbolic link or a Windows junction). A segment that doesn't exist yet is fine.
    /// </summary>
    private bool IsClean(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(rootFull, comparison))
            return false;

        var current = rootFull;
        foreach (var segment in full[rootFull.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                return false;
            current = Path.Combine(current, segment);
            if (IsLink(current))
                return false;
        }

        return true;
    }

    private void RequireClean(string path)
    {
        if (!IsClean(path))
            throw new ModStoreException("A folder of this mod is, or sits behind, a symbolic link, which Fleet doesn't follow.");
    }

    private static bool IsLink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
                return true;
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Deletes a folder (or just the link when it is one), ignoring a failure: a staging folder is only a leftover.</summary>
    private static void TryDelete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) && !IsLink(folder))
                return;
            if (IsLink(folder))
            {
                Directory.Delete(folder);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ThrowIfInvalid(string name)
    {
        if (!ModNames.IsValid(name))
            throw new ArgumentException($"'{name}' isn't a mod name.", nameof(name));
    }

    private static void ThrowIfInvalidSession(string sessionId)
    {
        if (!ModNames.IsValidSessionId(sessionId))
            throw new ArgumentException($"'{sessionId}' isn't a session id.", nameof(sessionId));
    }
}

/// <summary>What <c>versions.json</c> holds.</summary>
internal sealed class ModIndex
{
    public string? Name { get; set; }
    public int? Active { get; set; }
    public ModOff? Off { get; set; }
    public List<ModVersion>? Versions { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ModIndex))]
[JsonSerializable(typeof(Dictionary<string, ModOff>))]
internal sealed partial class ModIndexJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class ModStoreJsonContext : JsonSerializerContext;
