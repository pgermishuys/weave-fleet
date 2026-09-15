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
    public List<string> TargetTags { get; set; } = [];
    public string TargetType { get; set; } = "new_session";
    /// <summary>
    /// The IANA time zone a schedule's cron expression is read in, e.g. "Africa/Johannesburg".
    /// Null means UTC, which is how automations made before time zones were stored keep running.
    /// </summary>
    public string? TimeZone { get; set; }
    /// <summary>
    /// How a run gets its folder: "worktree" (a new worktree of <see cref="WorkspaceId"/> each run) or "existing" (the
    /// folder as it is, or a scratch folder when there is none). Null means the automation was made before this was
    /// stored and runs as it always has: in its folder, or in the first workspace root.
    /// </summary>
    public string? Isolation { get; set; }
    /// <summary>Where a run's worktree starts (<c>origin/main</c>); null means the repository's default.</summary>
    public string? BaseBranch { get; set; }
    /// <summary>
    /// When this automation's runs started being recorded, for automations made before runs were: the scheduler
    /// doesn't count anything earlier as missed. Null means since it was made. Set by migration 034 only.
    /// </summary>
    public string? HistoryStartsAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
    public string UserId { get; set; } = string.Empty;
}
