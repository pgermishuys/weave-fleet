using System.Text.Json;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>Fleet's file events for a harness that has no file watcher of its own (Claude Code, Pi).</summary>
internal static class FileEvents
{
    /// <summary>
    /// A file tool's finished call as Fleet's two file events: <c>files.written</c> (what progress tracking reads, with
    /// what the call did to the file, <paramref name="changes"/>, so a plan ticked by the very next call still shows this
    /// call's state) and <c>file.watcher.updated</c> (which Fleet turns into <c>files.changed</c>, so open files and the
    /// Changes panel reload).
    /// </summary>
    internal static IReadOnlyList<HarnessEvent> Written(string sessionId, string? messageId, string path, IReadOnlyList<FileChange> changes) =>
    [
        new()
        {
            Type = EventTypes.FilesWritten,
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(
                new FilesWrittenPayload { SessionId = sessionId, MessageId = messageId, Paths = [path], Changes = changes },
                InfrastructureJsonContext.Default.FilesWrittenPayload),
        },
        new()
        {
            Type = EventTypes.FileWatcherUpdated,
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = Changed(path),
        },
    ];

    /// <summary>A <c>file.watcher.updated</c> payload saying <paramref name="path"/> changed, as OpenCode's watcher says it.</summary>
    internal static JsonElement Changed(string path)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("file", path);
            json.WriteString("event", "change");
            json.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
