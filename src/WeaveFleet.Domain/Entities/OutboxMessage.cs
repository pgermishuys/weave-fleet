namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A committed outbox row used for ordered event delivery.
/// SQLite stores <see cref="Payload"/> as JSON text.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string? UserId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string AvailableAt { get; set; } = string.Empty;
    public string? DispatchedAt { get; set; }
}
