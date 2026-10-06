using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Builds Fleet's own context-window events (<see cref="EventTypes.ContextUsage"/>,
/// <see cref="EventTypes.ContextCompaction"/>), which every adapter sends for its session's own model calls and
/// compactions. The relay hands them to <see cref="Application.Services.SessionContextRecorder"/>; they never reach
/// the conversation.
/// </summary>
internal static class ContextEvents
{
    /// <summary>A model call's size, with the model's limits when the adapter knows them.</summary>
    internal static HarnessEvent Usage(ContextUsageReport report, string harnessSessionId, string? fleetSessionId = null)
        => Event(EventTypes.ContextUsage, JsonSerializer.SerializeToElement(report, InfrastructureJsonContext.Default.ContextUsageReport), harnessSessionId, fleetSessionId);

    /// <summary>A compaction started, ended or failed (<see cref="ContextCompactionPhases"/>).</summary>
    internal static HarnessEvent Compaction(string phase, string harnessSessionId, string? trigger = null, string? error = null, string? fleetSessionId = null)
        => Event(
            EventTypes.ContextCompaction,
            JsonSerializer.SerializeToElement(new ContextCompactionReport { Phase = phase, Trigger = trigger, Error = error }, InfrastructureJsonContext.Default.ContextCompactionReport),
            harnessSessionId,
            fleetSessionId);

    /// <summary>The report in a <see cref="EventTypes.ContextUsage"/> event's payload, or <see langword="null"/>.</summary>
    internal static ContextUsageReport? ReadUsage(HarnessEvent evt)
        => Read(evt, static payload => payload.Deserialize(InfrastructureJsonContext.Default.ContextUsageReport));

    /// <summary>The report in a <see cref="EventTypes.ContextCompaction"/> event's payload, or <see langword="null"/>.</summary>
    internal static ContextCompactionReport? ReadCompaction(HarnessEvent evt)
        => Read(evt, static payload => payload.Deserialize(InfrastructureJsonContext.Default.ContextCompactionReport) is { Phase.Length: > 0 } report ? report : null);

    private static T? Read<T>(HarnessEvent evt, Func<JsonElement, T?> read) where T : class
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        try
        {
            return read(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HarnessEvent Event(string type, JsonElement payload, string harnessSessionId, string? fleetSessionId) => new()
    {
        Type = type,
        SessionId = harnessSessionId,
        FleetSessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = payload,
    };
}
