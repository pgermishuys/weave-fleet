namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A callback registered to notify one session when another session completes.
/// </summary>
public sealed class SessionCallback
{
    public string Id { get; set; } = string.Empty;
    public string SourceSessionId { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = string.Empty;
    public string TargetInstanceId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string CreatedAt { get; set; } = string.Empty;
    public string? FiredAt { get; set; }
}
