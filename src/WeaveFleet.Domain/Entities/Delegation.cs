namespace WeaveFleet.Domain.Entities;

/// <summary>
/// Records that a parent session tool call delegated work to a child session.
/// </summary>
public sealed class Delegation
{
    public string Id { get; set; } = string.Empty;
    public string ParentSessionId { get; set; } = string.Empty;
    public string? ChildSessionId { get; set; }
    public string? ParentToolCallId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? CompletedAt { get; set; }
}
