namespace WeaveFleet.Domain.Entities;

/// <summary>
/// One run of an automation: one Fleet started, failed to start or skipped. Whether a started run is still going
/// comes from its session, so it isn't stored here.
/// </summary>
public sealed class AutomationRun
{
    public string Id { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    /// <summary>What started it: "schedule", "catch_up", "once", "manual", or the event type.</summary>
    public string Trigger { get; set; } = string.Empty;
    /// <summary>The schedule occurrence (UTC, ISO 8601) the run was for; null for Run now and events.</summary>
    public string? ScheduledFor { get; set; }
    public string StartedAt { get; set; } = string.Empty;
    /// <summary>What Fleet did: see <see cref="AutomationRunStatus"/>.</summary>
    public string Status { get; set; } = AutomationRunStatus.Starting;
    public string? SessionId { get; set; }
    public string? InstanceId { get; set; }
    /// <summary>The workflow run it started, for a <c>workflow</c> target; its state follows that run's.</summary>
    public string? WorkflowRunId { get; set; }
    /// <summary>Why it failed or was skipped, in words.</summary>
    public string? Error { get; set; }
}

public static class AutomationRunStatus
{
    public const string Starting = "starting";
    public const string Started = "started";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}
