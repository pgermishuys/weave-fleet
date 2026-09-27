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
/// <c>key: value</c> lines between <c>---</c> lines, then the note. People can read and edit them; a file Fleet can't
/// read is skipped, never deleted. What sessions read goes in <c>{data}/memory/{user}/context/</c>, one file per session
/// folder named by the SHA-256 of the folder's path, with <c>directories.json</c> saying which folder each is for.
/// Every write goes to a temporary file first and then replaces the old one, under one lock.
/// </summary>
internal sealed partial class FileMemoryStore(FleetOptions options, ILogger<FileMemoryStore> logger) : IMemoryStore, IDisposable
{
    private const string DirectoriesFile = "directories.json";

    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ReadNotes(userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string userId, MemoryNote note, CancellationToken ct = default)
    {
        if (!IsValidId(note.Id))
            throw new ArgumentException($"'{note.Id}' isn't a note id.", nameof(note));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WriteAtomically(Path.Combine(NotesFolder(userId), note.Id + ".md"), Format(note));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> DeleteAsync(string userId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var deleted = 0;
            foreach (var id in ids.Where(IsValidId).Distinct(StringComparer.Ordinal))
            {
                var path = Path.Combine(NotesFolder(userId), id + ".md");
                if (!File.Exists(path))
                    continue;
                File.Delete(path);
                deleted++;
            }

            return deleted;
        }
        finally
        {
            _lock.Release();
        }
    }

    public string ContextFolder(string userId) => Path.Combine(UserFolder(userId), "context");

    public async Task WriteContextAsync(string userId, string directory, string content, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = ContextFolder(userId);
            var directories = ReadDirectories(folder);
            foreach (var name in ContextNames(directory))
            {
                var path = Path.Combine(folder, name.Hash + ".md");
                if (!File.Exists(path) || File.ReadAllText(path) != content)
                    WriteAtomically(path, content);
                directories[name.Hash] = name.Directory;
            }

            WriteDirectories(folder, directories);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListContextDirectoriesAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. ReadDirectories(ContextFolder(userId)).Values.Distinct(StringComparer.Ordinal)];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearContextAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = ContextFolder(userId);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        finally
        {
            _lock.Release();
        }
    }

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

    private List<MemoryNote> ReadNotes(string userId)
    {
        var folder = NotesFolder(userId);
        if (!Directory.Exists(folder))
            return [];

        List<MemoryNote> notes = [];
        foreach (var path in Directory.EnumerateFiles(folder, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(id))
                continue;

            try
            {
                if (Parse(id, File.ReadAllText(path)) is { } note)
                    notes.Add(note);
                else
                    LogUnreadableNote(logger, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNoteReadFailed(logger, path, ex);
            }
        }

        return notes;
    }

    private Dictionary<string, string> ReadDirectories(string folder)
    {
        var path = Path.Combine(folder, DirectoriesFile);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllText(path), MemoryStoreJsonContext.Default.DictionaryStringString);
            return read is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            LogNoteReadFailed(logger, path, ex);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void WriteDirectories(string folder, Dictionary<string, string> directories)
        => WriteAtomically(Path.Combine(folder, DirectoriesFile), JsonSerializer.Serialize(directories, MemoryStoreJsonContext.Default.DictionaryStringString));

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

    /// <summary>Ids are lowercase hex, which keeps them from naming a path outside the notes folder.</summary>
    internal static bool IsValidId(string? id)
        => id is { Length: > 0 and <= 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped memory note {Path}: it isn't a note Fleet can read")]
    private static partial void LogUnreadableNote(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read memory file {Path}")]
    private static partial void LogNoteReadFailed(ILogger logger, string path, Exception exception);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class MemoryStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
