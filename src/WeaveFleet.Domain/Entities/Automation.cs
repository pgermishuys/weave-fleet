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
    /// <summary>The model runs use, as <c>provider/model</c>; null for the agent's or the harness's default.</summary>
    public string? Model { get; set; }
    /// <summary>The agent runs go to; null for the harness's default.</summary>
    public string? Agent { get; set; }
    /// <summary>
    /// The harness runs use, set with <see cref="Model"/> or <see cref="Agent"/> since they only mean something
    /// on the harness they were picked from. Null means the default harness at the time of the run.
    /// </summary>
    public string? HarnessType { get; set; }
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
    /// The workflow a <c>workflow</c> target runs (<c>builtin:…</c> or <c>repo:…</c>), in the repository
    /// <see cref="WorkspaceId"/> names. Null for every other target.
    /// </summary>
    public string? WorkflowId { get; set; }
    /// <summary>The workflow's optional steps switched on for its runs.</summary>
    public List<string> WorkflowSteps { get; set; } = [];
    /// <summary>
    /// When this automation's runs started being recorded, for automations made before runs were: the scheduler
    /// doesn't count anything earlier as missed. Null means since it was made. Set by migration 034 only.
    /// </summary>
    public string? HistoryStartsAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
    public string UserId { get; set; } = string.Empty;
}
