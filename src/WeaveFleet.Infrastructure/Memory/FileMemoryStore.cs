using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Memory;

namespace WeaveFleet.Infrastructure.Memory;

/// <summary>
/// Keeps each user's notes as Markdown files under <c>{data}/memory/{user}/notes/</c>, one per note: a few
/// <c>key: value</c> lines between <c>---</c> lines, then the note. The machine's notes are in <c>machine/</c>, each
/// repository's in <c>repositories/{key}/</c> (the key is from the repository's path), so a prompt reads two folders
/// whatever else there is. People can read and edit the files; a file Fleet can't read is skipped, never deleted.
/// <para>
/// Parsed notes are kept in memory, per file, with the file's time and size. Reading a folder looks at its files'
/// times and sizes and parses only the ones that changed, so a prompt costs a few file lookups, and a note edited by
/// hand is read again.
/// </para>
/// <para>
/// What sessions read goes in <c>{data}/memory/{user}/context/</c>: one file per session folder named by the SHA-256 of
/// the folder's path (the rules and the repository's notes), <c>machine.md</c> with the machine's notes that every
/// folder shares, and <c>folders.json</c> saying which folder and repository each file is for. Every write goes to a
/// temporary file first and then replaces the old one, under one lock.
/// </para>
/// </summary>
internal sealed partial class FileMemoryStore(FleetOptions options, ILogger<FileMemoryStore> logger) : IMemoryStore, IDisposable
{
    private const string FoldersFile = "folders.json";

    /// <summary>The machine's notes, which every session folder's file is read with. Fleet's plugins read it by this name.</summary>
    internal const string MachineFile = "machine.md";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Parsed notes per notes folder, by file path.</summary>
    private readonly Dictionary<string, Dictionary<string, CachedNote>> _notes = new(StringComparer.Ordinal);

    /// <summary>Each user's context index (hash → folder and repository), once read.</summary>
    private readonly Dictionary<string, Dictionary<string, MemoryContextFolder>> _contextIndex = new(StringComparer.Ordinal);

    /// <summary>What was last written to each context file, so an unchanged render isn't read back to compare.</summary>
    private readonly Dictionary<string, string> _contextWritten = new(StringComparer.Ordinal);

    private readonly HashSet<string> _migrated = new(StringComparer.Ordinal);

    public void Dispose() => _lock.Dispose();

    public Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default)
        => LockedAsync(() => (IReadOnlyList<MemoryNote>)AllFolders(userId).SelectMany(ReadFolder).ToList(), ct);

    public Task<IReadOnlyList<MemoryNote>> ListForAsync(string userId, string? repository, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            Migrate(userId);
            List<MemoryNote> notes = [.. ReadFolder(MachineFolder(userId)).Where(note => note.List == MemoryList.Machine)];
            if (repository is not null)
            {
                notes.AddRange(ReadFolder(RepositoryFolder(userId, repository))
                    .Where(note => note.List == MemoryList.Repository && note.Repository == repository));
            }

            return (IReadOnlyList<MemoryNote>)notes;
        }, ct);

    public Task<MemoryNote?> FindAsync(string userId, string id, CancellationToken ct = default)
        => LockedAsync(() => IsValidId(id) ? AllFolders(userId).SelectMany(ReadFolder).FirstOrDefault(note => note.Id == id) : null, ct);

    public Task SaveAsync(string userId, MemoryNote note, CancellationToken ct = default)
    {
        if (!IsValidId(note.Id))
            throw new ArgumentException($"'{note.Id}' isn't a note id.", nameof(note));
        if (note.List == MemoryList.Repository && string.IsNullOrWhiteSpace(note.Repository))
            throw new ArgumentException("A repository note names its repository.", nameof(note));

        return LockedAsync(() =>
        {
            Migrate(userId);
            var folder = note.List == MemoryList.Machine ? MachineFolder(userId) : RepositoryFolder(userId, note.Repository!);
            var path = Path.Combine(folder, note.Id + ".md");
            WriteAtomically(path, Format(note));
            Remember(folder, path, note);
            return true;
        }, ct);
    }

    public Task<int> DeleteAsync(string userId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var doomed = ids.Where(IsValidId).ToHashSet(StringComparer.Ordinal);
            var deleted = 0;
            foreach (var folder in AllFolders(userId))
            {
                foreach (var id in doomed)
                {
                    var path = Path.Combine(folder, id + ".md");
                    if (!File.Exists(path))
                        continue;
                    File.Delete(path);
                    if (_notes.TryGetValue(folder, out var cached))
                        cached.Remove(path);
                    deleted++;
                }
            }

            return deleted;
        }, ct);

    public string ContextFolder(string userId) => Path.Combine(UserFolder(userId), "context");

    public Task WriteContextAsync(string userId, string directory, string repository, string content, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var folder = ContextFolder(userId);
            var index = ContextIndex(userId);
            var changed = false;
            foreach (var name in ContextNames(directory))
            {
                var path = Path.Combine(folder, name.Hash + ".md");
                if (!(_contextWritten.TryGetValue(path, out var written) && written == content && File.Exists(path)))
                {
                    WriteAtomically(path, content);
                    _contextWritten[path] = content;
                }

                var entry = new MemoryContextFolder(name.Directory, repository);
                if (!index.TryGetValue(name.Hash, out var known) || known != entry)
                {
                    index[name.Hash] = entry;
                    changed = true;
                }
            }

            if (changed)
                WriteIndex(folder, index);
            return true;
        }, ct);

    public Task WriteMachineContextAsync(string userId, string content, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var path = Path.Combine(ContextFolder(userId), MachineFile);
            if (!(_contextWritten.TryGetValue(path, out var written) && written == content && File.Exists(path)))
            {
                WriteAtomically(path, content);
                _contextWritten[path] = content;
            }

            return true;
        }, ct);

    public Task<IReadOnlyList<MemoryContextFolder>> ListContextFoldersAsync(string userId, CancellationToken ct = default)
        => LockedAsync(() => (IReadOnlyList<MemoryContextFolder>)ContextIndex(userId).Values.Distinct().ToList(), ct);

    public Task ForgetContextAsync(string userId, string directory, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var folder = ContextFolder(userId);
            var index = ContextIndex(userId);
            foreach (var hash in index.Where(pair => pair.Value.Directory == directory).Select(pair => pair.Key).ToList())
            {
                var path = Path.Combine(folder, hash + ".md");
                if (File.Exists(path))
                    File.Delete(path);
                _contextWritten.Remove(path);
                index.Remove(hash);
            }

            WriteIndex(folder, index);
            return true;
        }, ct);

    public Task ClearContextAsync(string userId, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var folder = ContextFolder(userId);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            _contextIndex.Remove(userId);
            foreach (var path in _contextWritten.Keys.Where(path => path.StartsWith(folder, StringComparison.Ordinal)).ToList())
                _contextWritten.Remove(path);
            return true;
        }, ct);

    /// <summary>
    /// The file name a session folder's notes go under: the SHA-256 of the path, in lowercase hex. Fleet's plugins
    /// compute the same from the folder the harness gives them. The path as Fleet has it and its full, trimmed form
    /// both get a file, so a harness that normalises the path still finds it.
    /// </summary>
    internal static IEnumerable<(string Hash, string Directory)> ContextNames(string directory)
    {
        var full = Path.GetFullPath(directory);
        var trimmed = full.Length > 1 ? full.TrimEnd('/', '\\') : full;
        return new[] { directory, trimmed }
            .Distinct(StringComparer.Ordinal)
            .Select(path => (Hash(path), path));
    }

    internal static string Hash(string directory) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));

    internal static string Format(MemoryNote note)
    {
        var text = new StringBuilder();
        text.Append("---\n");
        text.Append("list: ").Append(note.List == MemoryList.Machine ? "machine" : "repository").Append('\n');
        text.Append("kind: ").Append(OneLine(note.Kind)).Append('\n');
        if (note.Repository is { } repository)
            text.Append("repository: ").Append(OneLine(repository)).Append('\n');
        if (note.SessionId is { } sessionId)
            text.Append("session: ").Append(OneLine(sessionId)).Append('\n');
        if (note.SessionTitle is { } title)
            text.Append("session-title: ").Append(OneLine(title)).Append('\n');
        text.Append("created: ").Append(note.Created.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("updated: ").Append(note.Updated.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("---\n");
        text.Append(note.Text.Trim()).Append('\n');
        return text.ToString();
    }

    /// <summary>The note in a file, or <see langword="null"/> when the file isn't one.</summary>
    internal static MemoryNote? Parse(string id, string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
            return null;

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0)
            return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines[1..end])
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = string.Join('\n', lines[(end + 1)..]).Trim();
        if (body.Length == 0)
            return null;

        MemoryList? list = fields.GetValueOrDefault("list") switch
        {
            "machine" => MemoryList.Machine,
            "repository" => MemoryList.Repository,
            _ => null,
        };
        if (list is null || (list == MemoryList.Repository && string.IsNullOrWhiteSpace(fields.GetValueOrDefault("repository"))))
            return null;

        var created = ParseTime(fields.GetValueOrDefault("created")) ?? DateTimeOffset.UnixEpoch;
        return new MemoryNote(
            id,
            list.Value,
            body,
            fields.GetValueOrDefault("kind") is { Length: > 0 } kind ? kind : MemoryKinds.Added,
            list == MemoryList.Repository ? fields["repository"] : null,
            NullIfEmpty(fields.GetValueOrDefault("session")),
            NullIfEmpty(fields.GetValueOrDefault("session-title")),
            created,
            ParseTime(fields.GetValueOrDefault("updated")) ?? created);
    }

    /// <summary>How many note files have been parsed: a read of an unchanged folder parses none.</summary>
    internal int ParseCount { get; private set; }

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

    /// <summary>The notes in a folder, parsing only the files whose time or size changed since the last read.</summary>
    private List<MemoryNote> ReadFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            _notes.Remove(folder);
            return [];
        }

        _notes.TryGetValue(folder, out var cached);
        var current = new Dictionary<string, CachedNote>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (!IsValidId(id))
                continue;

            current[file.FullName] = cached is not null
                                     && cached.TryGetValue(file.FullName, out var hit)
                                     && hit.Modified == file.LastWriteTimeUtc
                                     && hit.Length == file.Length
                ? hit
                : new CachedNote(file.LastWriteTimeUtc, file.Length, ReadNote(id, file.FullName));
        }

        _notes[folder] = current;
        return [.. current.Values.Select(entry => entry.Note).OfType<MemoryNote>()];
    }

    private MemoryNote? ReadNote(string id, string path)
    {
        ParseCount++;
        try
        {
            var note = Parse(id, File.ReadAllText(path));
            if (note is null)
                LogUnreadableNote(logger, path);
            return note;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNoteReadFailed(logger, path, ex);
            return null;
        }
    }

    /// <summary>Keeps a note Fleet just wrote, so the next read doesn't parse it again.</summary>
    private void Remember(string folder, string path, MemoryNote note)
    {
        if (!_notes.TryGetValue(folder, out var cached))
            return; // Read in full on first use.

        var file = new FileInfo(path);
        cached[path] = new CachedNote(file.LastWriteTimeUtc, file.Length, note);
    }

    /// <summary>The machine's folder, then every repository's.</summary>
    private IEnumerable<string> AllFolders(string userId)
    {
        Migrate(userId);
        yield return MachineFolder(userId);
        var repositories = Path.Combine(NotesFolder(userId), "repositories");
        if (!Directory.Exists(repositories))
            yield break;
        foreach (var folder in Directory.EnumerateDirectories(repositories))
            yield return folder;
    }

    /// <summary>
    /// Moves notes kept loose in the notes folder (the first layout) into their list's folder, once per user. A file
    /// that isn't a note stays where it is.
    /// </summary>
    private void Migrate(string userId)
    {
        if (!_migrated.Add(userId))
            return;

        var notes = NotesFolder(userId);
        if (!Directory.Exists(notes))
            return;

        foreach (var path in Directory.EnumerateFiles(notes, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(id) || ReadNote(id, path) is not { } note)
                continue;

            var folder = note.List == MemoryList.Machine ? MachineFolder(userId) : RepositoryFolder(userId, note.Repository!);
            Directory.CreateDirectory(folder);
            File.Move(path, Path.Combine(folder, id + ".md"), overwrite: true);
        }
    }

    private Dictionary<string, MemoryContextFolder> ContextIndex(string userId)
    {
        if (_contextIndex.TryGetValue(userId, out var index))
            return index;

        index = new Dictionary<string, MemoryContextFolder>(StringComparer.Ordinal);

        // The first layout's index had no repositories; the next prompts write this one.
        var legacy = Path.Combine(ContextFolder(userId), "directories.json");
        if (File.Exists(legacy))
            File.Delete(legacy);

        var path = Path.Combine(ContextFolder(userId), FoldersFile);
        if (File.Exists(path))
        {
            try
            {
                foreach (var (hash, entry) in JsonSerializer.Deserialize(File.ReadAllText(path), MemoryStoreJsonContext.Default.DictionaryStringMemoryContextFolder) ?? [])
                    index[hash] = entry;
            }
            catch (JsonException ex)
            {
                // Written again from the next prompts.
                LogNoteReadFailed(logger, path, ex);
            }
        }

        _contextIndex[userId] = index;
        return index;
    }

    private static void WriteIndex(string folder, Dictionary<string, MemoryContextFolder> index)
        => WriteAtomically(Path.Combine(folder, FoldersFile), JsonSerializer.Serialize(index, MemoryStoreJsonContext.Default.DictionaryStringMemoryContextFolder));

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private string UserFolder(string userId)
    {
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory;
        return Path.Combine(data, "memory", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16]);
    }

    private string NotesFolder(string userId) => Path.Combine(UserFolder(userId), "notes");

    private string MachineFolder(string userId) => Path.Combine(NotesFolder(userId), "machine");

    private string RepositoryFolder(string userId, string repository)
        => Path.Combine(NotesFolder(userId), "repositories", Hash(repository)[..16]);

    /// <summary>Ids are lowercase hex, which keeps them from naming a path outside the notes folder.</summary>
    internal static bool IsValidId(string? id)
        => id is { Length: > 0 and <= 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;

    private sealed record CachedNote(DateTime Modified, long Length, MemoryNote? Note);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped memory note {Path}: it isn't a note Fleet can read")]
    private static partial void LogUnreadableNote(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read memory file {Path}")]
    private static partial void LogNoteReadFailed(ILogger logger, string path, Exception exception);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, MemoryContextFolder>))]
internal sealed partial class MemoryStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
