namespace WeaveFleet.Domain.Entities;

/// <summary>
/// One row in the harness_events log: the canonical per-session record of every durable
/// harness event that was published. Used as the source for the
/// <c>/api/sessions/{id}/committed-events</c> gap-fill API.
/// </summary>
public sealed class HarnessEventLogEntry
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string? UserId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
