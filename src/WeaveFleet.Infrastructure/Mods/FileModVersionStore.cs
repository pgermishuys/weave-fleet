using System.Collections.Concurrent;
using System.Formats.Tar;
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
/// drafts and <c>off.json</c>. Keep takes the mod's lock, then the session's, always in that order.
/// Nothing from <c>{user16}</c> down is ever followed through a link, and only regular files are read or copied: a named
/// pipe or a device in a draft is refused (Keep) or skipped (Show code), and is never opened.
/// </summary>
public sealed class FileModVersionStore(string root) : IModVersionStore, IDisposable
{
    private const string IndexFile = "versions.json";
    private const string StoreFile = "store.json";
    private const string ManifestFile = "mod.json";
    private const string DraftsFolder = "drafts";
    private const string OffFile = "off.json";
    private const int MaxManifestBytes = 64 * 1024;

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

        var modLock = Lock(ModLock(userId, name));
        await modLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionLock = Lock(SessionLock(userId, sessionId));
            await sessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await KeepLockedAsync(userId, sessionId, name, source, check, ct).ConfigureAwait(false);
            }
            finally
            {
                sessionLock.Release();
            }
        }
        finally
        {
            modLock.Release();
        }
    }

    private async Task<ModVersion> KeepLockedAsync(string userId, string sessionId, string name, ModKeepSource source, ModKeepCheck check, CancellationToken ct)
    {
        var draft = DraftFolder(userId, sessionId, name);
        var modFolder = ModFolder(userId, name);
        if (!IsClean(draft) || !IsClean(modFolder))
            throw new ModStoreException("The draft or the mod's folder is, or sits behind, a symbolic link, which can't be kept.");
        if (!Directory.Exists(draft))
            throw new ModStoreException($"There's no draft of {name} in this session.");

        Directory.CreateDirectory(modFolder);
        DeleteLeftoverStaging(modFolder);

        var history = ReadIndex(userId, name);
        var number = NextNumber(modFolder, history);
        var staged = Path.Combine(modFolder, $"v{number}.{Guid.NewGuid():N}.tmp");
        var moved = false;
        string sha256;
        ModManifest manifest;
        JsonElement? report;
        try
        {
            CopyDraft(draft, staged);
            (manifest, sha256) = ValidateCopy(staged, name);
            report = await check(staged, ct).ConfigureAwait(false);
            if (ModChecks.Refusal(report) is { } refusal)
                throw new ModStoreException(refusal);

            Directory.Move(staged, Path.Combine(modFolder, $"v{number}"));
            moved = true;
        }
        finally
        {
            if (!moved)
                TryDelete(staged);
        }

        var version = new ModVersion(
            number,
            DateTimeOffset.UtcNow,
            manifest.Version,
            sha256,
            sessionId,
            Trimmed(source.SessionTitle),
            Trimmed(source.Note),
            report);
        var kept = Path.Combine(modFolder, $"v{number}");
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
        if (IsRegularFile(path))
        {
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(path), ModIndexJsonContext.Default.ModIndex);
            }
            catch (JsonException)
            {
            }
        }

        if (file?.Versions is null)
            return RebuildIndex(userId, name, path);

        var versions = file.Versions
            .Where(v => v is { Number: > 0, Version: not null, Sha256: not null })
            .OrderBy(v => v.Number)
            .ToList();
        var active = file.Active is { } number && versions.Any(v => v.Number == number)
            ? number
            : versions.Count == 0 ? (int?)null : versions[^1].Number;
        return new ModHistory(name, active, file.Off, versions);
    }

    /// <summary>
    /// A broken <c>versions.json</c> is moved aside and rebuilt from the <c>v{n}</c> folders, which each hold their
    /// <c>mod.json</c>: the history is never overwritten. What the index alone knew (session, note, check) is gone from the
    /// rebuilt copy, but not from the file that was moved aside.
    /// </summary>
    private ModHistory RebuildIndex(string userId, string name, string path)
    {
        File.Move(path, $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);

        var modFolder = ModFolder(userId, name);
        var versions = new List<ModVersion>();
        foreach (var folder in Directory.EnumerateDirectories(modFolder))
        {
            var folderName = Path.GetFileName(folder);
            if (!TryVersionNumber(folderName, out var number) || !IsClean(folder))
                continue;
            if (ParseManifest(folder) is not { } manifest || ModuleIn(folder, manifest.Hooks) is not { } module)
                continue;

            var info = new DirectoryInfo(folder);
            var created = info.CreationTimeUtc.Year > 2000 ? info.CreationTimeUtc : info.LastWriteTimeUtc;
            versions.Add(new ModVersion(number, new DateTimeOffset(created, TimeSpan.Zero), manifest.Version, Sha256Of(module), null, null, null, null));
        }

        if (versions.Count == 0)
            return ModHistory.Empty(name);

        versions.Sort((a, b) => a.Number.CompareTo(b.Number));
        return WriteIndex(userId, new ModHistory(name, versions[^1].Number, null, versions));
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

    /// <summary>What a crash between the copy and the move leaves: <c>v{n}.{guid}.tmp</c> folders. Gone on the next Keep.</summary>
    private static void DeleteLeftoverStaging(string modFolder)
    {
        foreach (var folder in Directory.EnumerateDirectories(modFolder, "v*.tmp"))
            TryDelete(folder);
    }

    private sealed class Tally
    {
        public int Files;
        public int Directories;
        public long Bytes;
    }

    /// <summary>
    /// Copies the draft to <paramref name="target"/> by walking it: links and anything but regular files and folders are
    /// refused, and the walk stops as soon as the file count or the bytes pass their limit. Nothing is followed.
    /// </summary>
    private static void CopyDraft(string draft, string target)
    {
        var tally = new Tally();
        Directory.CreateDirectory(target);
        Copy(new DirectoryInfo(draft), target);

        void Copy(DirectoryInfo source, string to)
        {
            foreach (var entry in source.EnumerateFileSystemInfos())
            {
                if (IsLink(entry.FullName))
                    throw new ModStoreException($"The draft contains a symbolic link ('{entry.Name}'), which can't be kept.");

                var destination = Path.Combine(to, entry.Name);
                if (entry is DirectoryInfo directory)
                {
                    if (++tally.Directories > ModStoreLimits.KeptFiles)
                        throw TooMany();
                    Directory.CreateDirectory(destination);
                    Copy(directory, destination);
                    continue;
                }

                if (!IsRegularFile(entry.FullName))
                    throw new ModStoreException($"The draft contains something that isn't a regular file ('{entry.Name}'), which can't be kept.");
                if (++tally.Files > ModStoreLimits.KeptFiles)
                    throw TooMany();

                using var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    tally.Bytes += read;
                    if (tally.Bytes > ModStoreLimits.KeptBytes)
                        throw new ModStoreException("The draft is larger than 16 MiB; a mod keeps at most 16 MiB.");
                    output.Write(buffer, 0, read);
                }
            }
        }

        static ModStoreException TooMany()
            => new($"The draft has more than {ModStoreLimits.KeptFiles} files; a mod keeps at most {ModStoreLimits.KeptFiles}.");
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
        if (!IsRegularFile(module))
            throw new ModStoreException($"mod.json's hooks file '{hooks}' isn't in the draft.");

        return (manifest, Sha256Of(module));
    }

    /// <summary>The hooks module of a kept folder when it's a regular file inside it; null otherwise.</summary>
    private static string? ModuleIn(string folder, string hooks)
    {
        try
        {
            if (Path.IsPathRooted(hooks))
                return null;
            var full = Path.GetFullPath(Path.Combine(folder, hooks));
            var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.Ordinal) && IsRegularFile(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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
            if (!File.Exists(path) || !IsRegularFile(path) || new FileInfo(path).Length > MaxManifestBytes)
                return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
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

                if (!IsRegularFile(entry.FullName) || ((FileInfo)entry).Length > ModStoreLimits.ShownFileBytes)
                    continue;

                string text;
                try
                {
                    text = StrictUtf8.GetString(File.ReadAllBytes(entry.FullName));
                }
                catch (Exception e) when (e is DecoderFallbackException or IOException or UnauthorizedAccessException)
                {
                    // Not text, or gone: Show code skips it.
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
        if (!IsClean(path) || !File.Exists(path) || !IsRegularFile(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ModIndexJsonContext.Default.DictionaryStringModOff) ?? [];
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

        if (IsRegularFile(path))
        {
            try
            {
                var store = JsonSerializer.Deserialize(File.ReadAllBytes(path), StoreJson.DictionaryStringJsonElement);
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

    /// <summary>
    /// True for a regular file only: not a link, a folder, a named pipe, a socket or a device. .NET reports a named pipe
    /// as an ordinary file and opening one waits for a writer, so the type is read without opening: on Unix by asking
    /// <see cref="TarWriter"/> to describe the path (it does an <c>lstat</c> and writes the type into the header, and we stop
    /// after that header); on Windows from the attributes.
    /// </summary>
    private static bool IsRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
                return false;
            if (OperatingSystem.IsWindows())
                return info.Exists && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

            var probe = new TarHeaderProbe();
            try
            {
                new TarWriter(probe, TarEntryFormat.Ustar, leaveOpen: true).WriteEntry(Path.GetFullPath(path), "x");
            }
            catch (TarHeaderProbe.Done)
            {
            }

            // A header for the entry "x", and its type flag: '0' (or NUL) is a regular file.
            return probe.Header is { Length: >= 157 } header && header[0] == (byte)'x' && header[1] == 0 && header[156] is (byte)'0' or 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Takes the first 512 bytes <see cref="TarWriter"/> writes (the header) and stops it there.</summary>
    private sealed class TarHeaderProbe : Stream
    {
        private readonly List<byte> _header = [];

        public sealed class Done : Exception;

        public byte[] Header => [.. _header];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_header.Count >= 512)
                throw new Done();
            _header.AddRange(buffer.ToArray());
            if (_header.Count >= 512)
                throw new Done();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
