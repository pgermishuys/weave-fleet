using System.Buffers;
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
/// call's result arrives. Fleet records it before answering the call, so it's here by then. A harness whose client
/// doesn't name its calls (GitHub Copilot's sends only a progress token) finds the record by the session, the tool and the
/// arguments instead, which its call's row also has.
/// </summary>
public sealed class FleetToolCallRecords
{
    /// <summary>A call whose result the adapter never saw (its process died) mustn't keep its record forever.</summary>
    internal const int MaxKept = 256;

    private readonly ConcurrentDictionary<string, FleetToolCallRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly Lock _unnamedLock = new();
    private readonly List<(string Key, FleetToolCallRecord Record)> _unnamed = [];

    public void Record(string callId, FleetToolCallRecord record)
    {
        _records[callId] = record;
        _order.Enqueue(callId);
        while (_order.Count > MaxKept && _order.TryDequeue(out var oldest))
            _records.TryRemove(oldest, out _);
    }

    /// <summary>The record for <paramref name="callId"/>, which is then forgotten; null when Fleet kept none.</summary>
    public FleetToolCallRecord? Take(string callId) => _records.TryRemove(callId, out var record) ? record : null;

    /// <summary>Keeps the record of a call its harness gave no id for, under its session, tool and arguments.</summary>
    public void Record(string fleetSessionId, string tool, JsonElement arguments, FleetToolCallRecord record)
    {
        lock (_unnamedLock)
        {
            _unnamed.Add((UnnamedKey(fleetSessionId, tool, arguments), record));
            if (_unnamed.Count > MaxKept)
                _unnamed.RemoveAt(0);
        }
    }

    /// <summary>
    /// The oldest record of a call with these arguments to <paramref name="tool"/> in the session, which is then forgotten;
    /// null when Fleet kept none. Arguments match whatever the order of their properties.
    /// </summary>
    public FleetToolCallRecord? Take(string fleetSessionId, string tool, JsonElement arguments)
    {
        var key = UnnamedKey(fleetSessionId, tool, arguments);
        lock (_unnamedLock)
        {
            var at = _unnamed.FindIndex(entry => entry.Key == key);
            if (at < 0)
                return null;
            var record = _unnamed[at].Record;
            _unnamed.RemoveAt(at);
            return record;
        }
    }

    private static string UnnamedKey(string fleetSessionId, string tool, JsonElement arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
            WriteSorted(json, arguments);
        return $"{fleetSessionId}\n{tool}\n{System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan)}";
    }

    /// <summary>The value as JSON with every object's properties in name order, so the same arguments read the same.</summary>
    private static void WriteSorted(Utf8JsonWriter json, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    json.WritePropertyName(property.Name);
                    WriteSorted(json, property.Value);
                }

                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteSorted(json, item);
                json.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                json.WriteStartObject();
                json.WriteEndObject();
                break;
            default:
                value.WriteTo(json);
                break;
        }
    }
}
