using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Builds Fleet's own running-work events (<see cref="EventTypes.WorkStarted"/>, <see cref="EventTypes.WorkUpdated"/>,
/// <see cref="EventTypes.WorkEnded"/>), which every adapter sends for the work its agent leaves running. The relay hands
/// them to <see cref="Services.RunningWorkRecorder"/>; they never reach the conversation.
/// </summary>
internal static class WorkEvents
{
    /// <summary>The first report of a piece of work.</summary>
    internal static HarnessEvent Started(WorkReport report, string harnessSessionId, string? fleetSessionId = null)
        => Event(EventTypes.WorkStarted, report, harnessSessionId, fleetSessionId);

    /// <summary>A later report: only what it sets changes.</summary>
    internal static HarnessEvent Updated(WorkReport report, string harnessSessionId, string? fleetSessionId = null)
        => Event(EventTypes.WorkUpdated, report, harnessSessionId, fleetSessionId);

    /// <summary>The work ended, with one of <see cref="WorkEndedReasons"/>.</summary>
    internal static HarnessEvent Ended(WorkReport report, string reason, string harnessSessionId, string? fleetSessionId = null)
        => Event(EventTypes.WorkEnded, report with { EndedReason = reason }, harnessSessionId, fleetSessionId);

    /// <summary>The report in a work event's payload, or <see langword="null"/>.</summary>
    internal static WorkReport? Read(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        try
        {
            var report = payload.Deserialize(InfrastructureJsonContext.Default.WorkReport);
            return string.IsNullOrWhiteSpace(report?.WorkId) ? null : report;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HarnessEvent Event(string type, WorkReport report, string harnessSessionId, string? fleetSessionId) => new()
    {
        Type = type,
        SessionId = harnessSessionId,
        FleetSessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(report, InfrastructureJsonContext.Default.WorkReport),
    };
}
