using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Builds Fleet's usage-limit event (<see cref="EventTypes.HarnessUsage"/>), which an adapter sends when its harness
/// says how much of its account's limits are used. The relay hands it to
/// <see cref="Application.Harnesses.HarnessUsageLimits"/>; it never reaches the conversation.
/// </summary>
internal static class UsageLimitEvents
{
    internal static HarnessEvent Usage(UsageLimitReport report, string harnessSessionId) => new()
    {
        Type = EventTypes.HarnessUsage,
        SessionId = harnessSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(report, InfrastructureJsonContext.Default.UsageLimitReport),
    };

    /// <summary>The report in a <see cref="EventTypes.HarnessUsage"/> event's payload, or <see langword="null"/>.</summary>
    internal static UsageLimitReport? Read(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        try
        {
            return payload.Deserialize(InfrastructureJsonContext.Default.UsageLimitReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
