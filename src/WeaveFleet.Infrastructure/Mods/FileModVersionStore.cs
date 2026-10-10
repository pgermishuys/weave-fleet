using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Infrastructure.Mods;

/// <summary>
/// Keeps each user's mods under <c>{data}/mods/{user}/</c>:
/// <list type="bullet">
/// <item><c>{name}/versions.json</c>: the versions, which one sessions load, and whether the mod is off.</item>
/// <item><c>{name}/v{n}/</c>: a copy of the draft when it was kept, never changed once written.</item>
/// <item><c>{name}/store.json</c>: the mod's <c>$.store</c>, shared by its drafts and its kept versions.</item>
/// <item><c>drafts/{session}/{name}/</c>: what the agent is writing, and <c>off.json</c> beside them for the drafts that are off.</item>
/// </list>
/// </summary>
public sealed class FileModVersionStore(string root) : IModVersionStore, IDisposable
{
    private const string IndexFile = "versions.json";
    private const string StoreFile = "store.json";
    private const string ManifestFile = "mod.json";
    private const string DraftsFolder = "drafts";
    private const string OffFile = "off.json";
    private const int MaxKeepFiles = 500;
    private const long MaxKeepBytes = 16L * 1024 * 1024;
    private const int MaxKeyLength = 1024;

    private static readonly string[] ModuleExtensions = [".js", ".mjs", ".ts", ".mts"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileModVersionStore(FleetOptions options)
        : this(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory, "mods"))
    {
    }

    public void Dispose() => _lock.Dispose();

    public Task<ModHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => LockedAsync(() => ReadIndex(userId, name), ct);

    public Task<IReadOnlyList<ModHistory>> ListAsync(string userId, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var folder = UserFolder(userId);
            if (!Directory.Exists(folder))
                return (IReadOnlyList<ModHistory>)[];

            return Directory.EnumerateDirectories(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(ModNames.IsValid)
                .Select(name => ReadIndex(userId, name))
                .Where(history => history.Versions.Count > 0)
                .OrderBy(history => history.Name, StringComparer.Ordinal)
                .ToList();
        }, ct);

    public Task<ModVersion> KeepAsync(string userId, string sessionId, string name, ModKeepSource source, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        ThrowIfInvalidSession(sessionId);
        return LockedAsync(() =>
        {
            var draft = DraftFolder(userId, sessionId, name);
            if (!Directory.Exists(draft))
                throw new ModStoreException($"There's no draft of {name} in this session.");

            var sha256 = Validate(draft, name);
            var history = ReadIndex(userId, name);
            var manifest = ParseManifest(draft)!;
            var modFolder = ModFolder(userId, name);
            var number = NextNumber(modFolder, history);

            var temporary = Path.Combine(modFolder, $"v{number}.{Guid.NewGuid():N}.tmp");
            try
            {
                CopyFolder(new DirectoryInfo(draft), temporary);
                Directory.Move(temporary, Path.Combine(modFolder, $"v{number}"));
            }
            catch
            {
                if (Directory.Exists(temporary))
                    Directory.Delete(temporary, recursive: true);
                throw;
            }

            var version = new ModVersion(
                number,
                DateTimeOffset.UtcNow,
                manifest.Version,
                sha256,
                sessionId,
                Trimmed(source.SessionTitle),
                Trimmed(source.Note),
                source.Check);
            WriteIndex(userId, new ModHistory(name, number, null, [.. history.Versions, version]));

            Directory.Delete(draft, recursive: true);
            WriteDraftOff(userId, sessionId, name, null);
            var session = SessionDrafts(userId, sessionId);
            if (!Directory.EnumerateFileSystemEntries(session).Any())
                Directory.Delete(session);
            return version;
        }, ct);
    }

    public Task SetActiveAsync(string userId, string name, int number, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(() =>
        {
            var history = ReadIndex(userId, name);
            if (history.Versions.All(v => v.Number != number))
                throw new ModStoreException($"{name} has no version {number}.");
            WriteIndex(userId, history with { Active = number });
            return true;
        }, ct);
    }

    public Task SetOffAsync(string userId, string name, ModOff? off, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(() =>
        {
            var history = ReadIndex(userId, name);
            if (history.Versions.Count == 0)
                throw new ModStoreException($"{name} hasn't been kept yet.");
            WriteIndex(userId, history with { Off = off });
            return true;
        }, ct);
    }

    public string VersionFolder(string userId, string name, int number)
    {
        ThrowIfInvalid(name);
        return Path.Combine(ModFolder(userId, name), $"v{number}");
    }

    public Task<ModManifest?> ReadManifestAsync(string folder, CancellationToken ct = default)
        => LockedAsync(() => ParseManifest(folder), ct);

    public Task<IReadOnlyList<ModFile>?> ReadVersionFilesAsync(string userId, string name, int number, CancellationToken ct = default)
        => LockedAsync<IReadOnlyList<ModFile>?>(() => ModNames.IsValid(name) ? ReadFiles(VersionFolder(userId, name, number)) : null, ct);

    // Drafts

    public Task<IReadOnlyList<ModDraft>> ListDraftsAsync(string userId, string sessionId, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            if (!ModNames.IsValidSessionId(sessionId))
                return (IReadOnlyList<ModDraft>)[];

            var session = SessionDrafts(userId, sessionId);
            if (!Directory.Exists(session))
                return [];

            var off = ReadDraftOff(userId, sessionId);
            return Directory.EnumerateDirectories(session)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(ModNames.IsValid)
                .Order(StringComparer.Ordinal)
                .Select(name => new ModDraft(sessionId, name, Path.Combine(session, name), off.GetValueOrDefault(name)))
                .ToList();
        }, ct);

    public Task<ModDraft?> GetDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            if (!ModNames.IsValid(name) || !ModNames.IsValidSessionId(sessionId))
                return null;
            var folder = DraftFolder(userId, sessionId, name);
            return Directory.Exists(folder) ? new ModDraft(sessionId, name, folder, ReadDraftOff(userId, sessionId).GetValueOrDefault(name)) : null;
        }, ct);

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
        return LockedAsync(() =>
        {
            if (off is not null && !Directory.Exists(DraftFolder(userId, sessionId, name)))
                throw new ModStoreException($"There's no draft of {name} in this session.");
            WriteDraftOff(userId, sessionId, name, off);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<ModFile>?> ReadDraftFilesAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => LockedAsync<IReadOnlyList<ModFile>?>(() => ModNames.IsValid(name) && ModNames.IsValidSessionId(sessionId) ? ReadFiles(DraftFolder(userId, sessionId, name)) : null, ct);

    // $.store

    public Task<JsonElement?> GetValueAsync(string userId, string name, string key, CancellationToken ct = default)
        => LockedAsync(() =>
            ModNames.IsValid(name) && ReadStore(userId, name, out _).TryGetValue(key, out var value) ? (JsonElement?)value : null, ct);

    public Task SetValueAsync(string userId, string name, string key, JsonElement value, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        if (key.Length is 0 or > MaxKeyLength)
            throw new ArgumentException($"A store key is 1 to {MaxKeyLength} characters.", nameof(key));
        return LockedAsync(() =>
        {
            var store = ReadStore(userId, name, out var broken);
            store[key] = value;
            var json = JsonSerializer.SerializeToUtf8Bytes(store, ModStoreJsonContext.Default.DictionaryStringJsonElement);
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
        return LockedAsync(() =>
        {
            var store = ReadStore(userId, name, out var broken);
            if (store.Remove(key))
                WriteStore(userId, name, JsonSerializer.SerializeToUtf8Bytes(store, ModStoreJsonContext.Default.DictionaryStringJsonElement), broken);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<string>> KeysAsync(string userId, string name, CancellationToken ct = default)
        => LockedAsync(() => ModNames.IsValid(name)
            ? (IReadOnlyList<string>)ReadStore(userId, name, out _).Keys.Order(StringComparer.Ordinal).ToList()
            : [], ct);

    // Index

    private ModHistory ReadIndex(string userId, string name)
    {
        if (!ModNames.IsValid(name))
            return ModHistory.Empty(name);

        var path = Path.Combine(ModFolder(userId, name), IndexFile);
        if (!File.Exists(path))
            return ModHistory.Empty(name);

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), ModIndexJsonContext.Default.ModIndex);
            var versions = (file?.Versions ?? [])
                .Where(v => v is { Number: > 0, Version: not null, Sha256: not null })
                .OrderBy(v => v.Number)
                .ToList();
            var active = file?.Active is { } number && versions.Any(v => v.Number == number) ? number : (int?)null;
            return new ModHistory(name, active, file?.Off, versions);
        }
        catch (JsonException)
        {
            // A file someone broke by hand: the mod reads as not kept until it's fixed. The versions are still in the v{n} folders.
            return ModHistory.Empty(name);
        }
    }

    private void WriteIndex(string userId, ModHistory history)
    {
        var file = new ModIndex { Name = history.Name, Active = history.Active, Off = history.Off, Versions = [.. history.Versions] };
        WriteAtomically(
            Path.Combine(ModFolder(userId, history.Name), IndexFile),
            JsonSerializer.Serialize(file, ModIndexJsonContext.Default.ModIndex));
    }

    private static int NextNumber(string modFolder, ModHistory history)
    {
        var highest = history.Versions.Count == 0 ? 0 : history.Versions.Max(v => v.Number);
        if (Directory.Exists(modFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(modFolder))
            {
                var folderName = Path.GetFileName(folder);
                if (folderName.Length > 1 && folderName[0] == 'v' && int.TryParse(folderName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    highest = Math.Max(highest, n);
            }
        }

        return highest + 1;
    }

    // Keep

    /// <summary>Checks the draft can be kept and returns the SHA-256 of its hooks module.</summary>
    private static string Validate(string draft, string name)
    {
        var folder = new DirectoryInfo(draft);
        if (folder.LinkTarget is not null)
            throw new ModStoreException("The draft is a symbolic link, which can't be kept.");

        var manifestPath = Path.Combine(draft, ManifestFile);
        if (!File.Exists(manifestPath))
            throw new ModStoreException("The draft has no mod.json.");
        var manifest = ParseManifest(draft)
            ?? throw new ModStoreException("mod.json isn't valid JSON with a name, version, description and hooks.");
        if (manifest.Name != name)
            throw new ModStoreException($"mod.json names the mod '{manifest.Name}', but the draft's folder is '{name}'.");

        var hooks = manifest.Hooks;
        if (Path.IsPathRooted(hooks) || hooks.StartsWith('/') || hooks.StartsWith('\\'))
            throw new ModStoreException("mod.json's hooks must be a path inside the mod's folder, not an absolute one.");

        string module;
        try
        {
            var full = Path.GetFullPath(Path.Combine(draft, hooks));
            var prefix = Path.GetFullPath(draft).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

        var files = 0;
        long bytes = 0;
        Scan(folder);

        if (!File.Exists(module))
            throw new ModStoreException($"mod.json's hooks file '{hooks}' isn't in the draft.");
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(module)));

        void Scan(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                    throw new ModStoreException($"The draft contains a symbolic link ('{entry.Name}'), which can't be kept.");
                if (entry is DirectoryInfo child)
                {
                    Scan(child);
                    continue;
                }

                files++;
                bytes += ((FileInfo)entry).Length;
                if (files > MaxKeepFiles)
                    throw new ModStoreException($"The draft has more than {MaxKeepFiles} files; a mod keeps at most {MaxKeepFiles}.");
                if (bytes > MaxKeepBytes)
                    throw new ModStoreException("The draft is larger than 16 MiB; a mod keeps at most 16 MiB.");
            }
        }
    }

    private static void CopyFolder(DirectoryInfo source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in source.EnumerateFiles())
            file.CopyTo(Path.Combine(target, file.Name));
        foreach (var directory in source.EnumerateDirectories())
            CopyFolder(directory, Path.Combine(target, directory.Name));
    }

    private static ModManifest? ParseManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestFile);
        if (!File.Exists(path))
            return null;

        try
        {
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

    private static List<ModFile>? ReadFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return null;

        var files = new List<ModFile>();
        Walk(new DirectoryInfo(folder), "");
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        void Walk(DirectoryInfo directory, string prefix)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                    continue;
                if (entry is DirectoryInfo child)
                {
                    Walk(child, $"{prefix}{child.Name}/");
                    continue;
                }

                var file = (FileInfo)entry;
                if (file.Length > ModStoreLimits.ShownFileBytes)
                    continue;
                try
                {
                    files.Add(new ModFile($"{prefix}{file.Name}", StrictUtf8.GetString(File.ReadAllBytes(file.FullName))));
                }
                catch (DecoderFallbackException)
                {
                    // Not text: Show code skips it.
                }
            }
        }
    }

    // Draft off state

    private Dictionary<string, ModOff> ReadDraftOff(string userId, string sessionId)
    {
        var path = Path.Combine(SessionDrafts(userId, sessionId), OffFile);
        if (!File.Exists(path))
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
        var entries = ReadDraftOff(userId, sessionId);
        if (off is null && !entries.Remove(name))
            return;
        if (off is not null)
            entries[name] = off;

        var path = Path.Combine(SessionDrafts(userId, sessionId), OffFile);
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
        if (!File.Exists(path))
            return [];

        try
        {
            var store = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModStoreJsonContext.Default.DictionaryStringJsonElement);
            if (store is not null)
                return store;
        }
        catch (JsonException)
        {
        }

        broken = true;
        return [];
    }

    private void WriteStore(string userId, string name, byte[] json, bool broken)
    {
        var path = Path.Combine(ModFolder(userId, name), StoreFile);
        if (broken && File.Exists(path))
            File.Move(path, $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    // Plumbing

    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LockedAsync(Func<bool> action, CancellationToken ct) => await LockedAsync<bool>(action, ct).ConfigureAwait(false);

    private string UserFolder(string userId)
        => Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16]);

    private string ModFolder(string userId, string name) => Path.Combine(UserFolder(userId), name);

    private string SessionDrafts(string userId, string sessionId) => Path.Combine(UserFolder(userId), DraftsFolder, sessionId);

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
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
