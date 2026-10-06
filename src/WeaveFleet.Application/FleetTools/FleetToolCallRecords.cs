using System.Collections.Concurrent;
using System.Text.Json;

namespace WeaveFleet.Application.FleetTools;

/// <summary>What Fleet keeps with one of its tool calls for showing it: the tool card's title, and the call's metadata.</summary>
/// <param name="Metadata">
/// The same object the OpenCode plugins store with the call: <c>canvasId</c>, <c>version</c>, and the <c>screenshot</c>
/// Fleet kept for the conversation.
/// </param>
public sealed record FleetToolCallRecord(string Title, JsonElement Metadata);

/// <summary>
/// The title and metadata of Fleet's tool calls made over MCP, until the harness's adapter picks them up. An MCP result
/// only carries what the model reads, so the adapter takes the rest from here, by the harness's id for the call, when the
/// call's result arrives. Fleet records it before answering the call, so it's here by then.
/// </summary>
public sealed class FleetToolCallRecords
{
    /// <summary>A call whose result the adapter never saw (its process died) mustn't keep its record forever.</summary>
    internal const int MaxKept = 256;

    private readonly ConcurrentDictionary<string, FleetToolCallRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public void Record(string callId, FleetToolCallRecord record)
    {
        _records[callId] = record;
        _order.Enqueue(callId);
        while (_order.Count > MaxKept && _order.TryDequeue(out var oldest))
            _records.TryRemove(oldest, out _);
    }

    /// <summary>The record for <paramref name="callId"/>, which is then forgotten; null when Fleet kept none.</summary>
    public FleetToolCallRecord? Take(string callId) => _records.TryRemove(callId, out var record) ? record : null;
}
