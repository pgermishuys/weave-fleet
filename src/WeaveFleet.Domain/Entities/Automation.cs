namespace WeaveFleet.Domain.Entities;

/// <summary>
/// An automation that can be triggered by schedule or event to run an agent task.
/// </summary>
public sealed class Automation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string TriggerConfig { get; set; } = string.Empty;
    public int MaxConcurrentRuns { get; set; }
    public int MaxRunsPerHour { get; set; }
    public int TimeoutMinutes { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDeleted { get; set; }
    public string? WorkspaceId { get; set; }
    public string? Model { get; set; }
    public string? Agent { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
    public string UserId { get; set; } = string.Empty;
}
