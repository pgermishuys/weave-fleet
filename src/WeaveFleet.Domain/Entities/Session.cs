namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A conversation session between the user and an opencode instance, optionally linked to a project.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string OpencodeSessionId { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled";
    public string Status { get; set; } = "active";
    public string Directory { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? StoppedAt { get; set; }
    public string? ParentSessionId { get; set; }
    public string? ActivityStatus { get; set; }
    public string? LifecycleStatus { get; set; }
    public int TotalTokens { get; set; }
    public double TotalCost { get; set; }
}
