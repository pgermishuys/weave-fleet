using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace NuCode.Audit;

/// <summary>
/// Writes audit entries as append-only JSONL lines to per-session files under
/// <c>{workingDirectory}/.nucode/audit/{sessionId}.jsonl</c>.
/// Each session gets its own <see cref="Channel{T}"/> so the calling thread is never blocked on I/O.
/// </summary>
internal sealed class JsonlAuditService : IAuditService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _workingDirectory;
    private readonly ConcurrentDictionary<string, (Channel<string> Channel, Task WriteTask)> _sessions = new();

    public JsonlAuditService(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Returns the audit file path for a given session and working directory.
    /// Sanitises the session ID to prevent path traversal.
    /// </summary>
    public static string BuildFilePath(string workingDirectory, string sessionId)
    {
        var sanitised = Path.GetFileName(sessionId);
        if (string.IsNullOrEmpty(sanitised) || sanitised != sessionId)
        {
            throw new ArgumentException(
                $"Session ID '{sessionId}' contains invalid path characters.", nameof(sessionId));
        }

        return Path.Combine(workingDirectory, ".nucode", "audit", $"{sanitised}.jsonl");
    }

    /// <inheritdoc/>
    public Task RecordToolInvocationAsync(AuditEntry entry)
    {
        GetOrCreateChannel(entry.SessionId).TryWrite(
            JsonSerializer.Serialize(entry, s_jsonOptions));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RecordPermissionDecisionAsync(AuditPermissionEntry entry)
    {
        GetOrCreateChannel(entry.SessionId).TryWrite(
            JsonSerializer.Serialize(entry, s_jsonOptions));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var (channel, _) in _sessions.Values)
        {
            channel.Writer.TryComplete();
        }

        foreach (var (_, writeTask) in _sessions.Values)
        {
            await writeTask.ConfigureAwait(false);
        }
    }

    private ChannelWriter<string> GetOrCreateChannel(string sessionId)
    {
        var entry = _sessions.GetOrAdd(sessionId, sid =>
        {
            var filePath = BuildFilePath(_workingDirectory, sid);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var channel = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true });
            var task = WriteLoopAsync(filePath, channel.Reader);
            return (channel, task);
        });
        return entry.Channel.Writer;
    }

    private static async Task WriteLoopAsync(string filePath, ChannelReader<string> reader)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream);

        await foreach (var line in reader.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }
}
