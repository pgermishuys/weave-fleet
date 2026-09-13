using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Terminals;

/// <summary>
/// Files under <c>{root}/{sessionId}/</c>: <c>index.json</c> lists the terminals, and <c>{terminalId}.log</c>
/// holds each one's scrollback (already cleaned by <see cref="TerminalReplaySanitizer"/>).
/// </summary>
internal sealed partial class TerminalHistoryStore(string root) : ITerminalHistoryStore
{
    private const string IndexFile = "index.json";
    private const string LogExtension = ".log";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SavedTerminal>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var path = Path.Combine(SessionDirectory(sessionId), IndexFile);
        var entries = await ReadIndexAsync(path, ct).ConfigureAwait(false);
        return [.. entries.Select(e => new SavedTerminal(e.Id, e.Title, e.CreatedAt)).OrderBy(t => t.CreatedAt)];
    }

    public async Task SaveTerminalAsync(string sessionId, SavedTerminal terminal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        EnsureValidId(terminal.Id, nameof(terminal));
        await UpdateIndexAsync(sessionId, entries =>
        {
            entries.RemoveAll(e => e.Id == terminal.Id);
            entries.Add(new TerminalIndexEntry { Id = terminal.Id, Title = terminal.Title, CreatedAt = terminal.CreatedAt });
        }, ct).ConfigureAwait(false);
    }

    public async Task RemoveTerminalAsync(string sessionId, string terminalId, CancellationToken ct = default)
    {
        EnsureValidId(terminalId, nameof(terminalId));
        await UpdateIndexAsync(sessionId, entries => entries.RemoveAll(e => e.Id == terminalId), ct).ConfigureAwait(false);
        TryDelete(LogPath(sessionId, terminalId));
    }

    public async Task<byte[]?> ReadHistoryAsync(string sessionId, string terminalId, CancellationToken ct = default)
    {
        var path = LogPath(sessionId, terminalId);
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task WriteHistoryAsync(string sessionId, string terminalId, ReadOnlyMemory<byte> history, CancellationToken ct = default)
    {
        var path = LogPath(sessionId, terminalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicAsync(path, history, ct).ConfigureAwait(false);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var dir = SessionDirectory(sessionId);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        _indexLocks.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    private async Task UpdateIndexAsync(string sessionId, Action<List<TerminalIndexEntry>> change, CancellationToken ct)
    {
        var dir = SessionDirectory(sessionId);
        var gate = _indexLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(dir, IndexFile);
            var entries = await ReadIndexAsync(path, ct).ConfigureAwait(false);
            change(entries);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.SerializeToUtf8Bytes(entries, TerminalStoreJsonContext.Default.ListTerminalIndexEntry);
            await WriteAtomicAsync(path, json, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<List<TerminalIndexEntry>> ReadIndexAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, TerminalStoreJsonContext.Default.ListTerminalIndexEntry, ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (JsonException)
        {
            // A damaged index loses the tab list, not the session.
            return [];
        }
    }

    private static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string SessionDirectory(string sessionId)
    {
        EnsureValidId(sessionId, nameof(sessionId));
        return Path.Combine(root, sessionId);
    }

    private string LogPath(string sessionId, string terminalId)
    {
        EnsureValidId(terminalId, nameof(terminalId));
        return Path.Combine(SessionDirectory(sessionId), terminalId + LogExtension);
    }

    private static void EnsureValidId(string id, string paramName)
    {
        if (string.IsNullOrEmpty(id) || !SafeId().IsMatch(id) || id is "." or "..")
            throw new ArgumentException($"'{id}' can't be used as a terminal file name.", paramName);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex SafeId();
}

internal sealed class TerminalIndexEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
}

[JsonSerializable(typeof(List<TerminalIndexEntry>))]
internal sealed partial class TerminalStoreJsonContext : JsonSerializerContext;
