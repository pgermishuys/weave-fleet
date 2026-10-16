using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Events Fleet raises for a session itself, such as the session waiting on the user for a page's pick. The relay merges
/// them into the session's harness event stream, so they go everywhere a harness's own event goes: the activity tracker,
/// the sidebar, the conversation and notifications. While no harness process serves the session they're dropped.
/// </summary>
public sealed class FleetSessionEvents
{
    private readonly ConcurrentDictionary<string, ChannelWriter<HarnessEvent>> _streams = new(StringComparer.Ordinal);

    /// <summary>Raises the session's activity status (<see cref="ActivityStatuses"/>); false when nothing serves the session now.</summary>
    public bool RaiseStatus(string fleetSessionId, string status)
        => _streams.TryGetValue(fleetSessionId, out var stream) && stream.TryWrite(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = fleetSessionId,
            FleetSessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(
                new FleetStatusPayload(new FleetStatusKind(status)), FleetSessionEventsJsonContext.Default.FleetStatusPayload),
        });

    /// <summary>The relay's side: what's raised for the session goes to <paramref name="stream"/> until the handle is disposed.</summary>
    public IDisposable Attach(string fleetSessionId, ChannelWriter<HarnessEvent> stream)
    {
        _streams[fleetSessionId] = stream;
        return new Detach(() => _streams.TryRemove(new KeyValuePair<string, ChannelWriter<HarnessEvent>>(fleetSessionId, stream)));
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        public void Dispose() => detach();
    }
}

internal sealed record FleetStatusPayload([property: System.Text.Json.Serialization.JsonPropertyName("status")] FleetStatusKind Status);

internal sealed record FleetStatusKind([property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type);

[System.Text.Json.Serialization.JsonSerializable(typeof(FleetStatusPayload))]
internal sealed partial class FleetSessionEventsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
