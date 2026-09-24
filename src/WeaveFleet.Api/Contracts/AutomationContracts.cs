namespace WeaveFleet.Api.Contracts;

public sealed record CreateAutomationRequest(
    string Name,
    string Prompt,
    string TriggerType,
    string TriggerConfig,
    int MaxConcurrentRuns = 1,
    int MaxRunsPerHour = 10,
    int TimeoutMinutes = 30,
    string? WorkspaceId = null,
    string? Model = null,
    string? Agent = null,
    List<string>? TargetTags = null,
    string? TargetType = null,
    string? TimeZone = null,
    string? Isolation = null,
    string? BaseBranch = null,
    /// <summary>
    /// The harness <c>Model</c> and <c>Agent</c> were picked from, ignored without either; for a <c>workflow</c> target,
    /// the harness its runs use (null: the default harness when it fires).
    /// </summary>
    string? HarnessType = null,
    /// <summary>For a <c>workflow</c> target: the workflow it runs (<c>builtin:…</c> or <c>repo:…</c>).</summary>
    string? WorkflowId = null,
    /// <summary>For a <c>workflow</c> target: the optional steps switched on.</summary>
    List<string>? WorkflowSteps = null);

public sealed record UpdateAutomationRequest(
    string Name,
    string Prompt,
    string TriggerType,
    string TriggerConfig,
    int MaxConcurrentRuns = 1,
    int MaxRunsPerHour = 10,
    int TimeoutMinutes = 30,
    string? WorkspaceId = null,
    string? Model = null,
    string? Agent = null,
    List<string>? TargetTags = null,
    string? TargetType = null,
    string? TimeZone = null,
    string? Isolation = null,
    string? BaseBranch = null,
    /// <summary>
    /// The harness <c>Model</c> and <c>Agent</c> were picked from, ignored without either; for a <c>workflow</c> target,
    /// the harness its runs use (null: the default harness when it fires).
    /// </summary>
    string? HarnessType = null,
    /// <summary>For a <c>workflow</c> target: the workflow it runs (<c>builtin:…</c> or <c>repo:…</c>).</summary>
    string? WorkflowId = null,
    /// <summary>For a <c>workflow</c> target: the optional steps switched on.</summary>
    List<string>? WorkflowSteps = null);

public sealed record AutomationResponse(
    string Id,
    string Name,
    string Prompt,
    string TriggerType,
    string TriggerConfig,
    int MaxConcurrentRuns,
    int MaxRunsPerHour,
    int TimeoutMinutes,
    bool IsEnabled,
    string? WorkspaceId,
    string? Model,
    string? Agent,
    string CreatedAt,
    string? UpdatedAt,
    List<string>? TargetTags,
    string TargetType,
    string? TimeZone,
    string? Isolation,
    string? BaseBranch,
    /// <summary>The harness runs use; null for the default harness at the time of the run.</summary>
    string? HarnessType,
    /// <summary>The workflow a <c>workflow</c> target runs; null for other targets.</summary>
    string? WorkflowId,
    /// <summary>The workflow's optional steps switched on.</summary>
    List<string>? WorkflowSteps,
    /// <summary>When it runs next (UTC, ISO 8601); null when it's off or waits for an event.</summary>
    string? NextRunAt,
    AutomationRunResponse? LastRun);

/// <summary>
/// One run. State is "starting", "running", "done", "failed" or "skipped"; a run that started a workflow run follows it,
/// and can also be "waiting" (on you) or "ended".
/// </summary>
public sealed record AutomationRunResponse(
    string Id,
    string AutomationId,
    string Trigger,
    string? ScheduledFor,
    string StartedAt,
    string State,
    string? SessionId,
    string? InstanceId,
    string? Error,
    /// <summary>The workflow run it started, for a <c>workflow</c> target.</summary>
    string? WorkflowRunId = null);

public sealed record AutomationRunListResponse(IReadOnlyList<AutomationRunResponse> Runs);

/// <summary>A new automation's starting point from a session: its first message and where it ran.</summary>
public sealed record AutomationDraftResponse(string Prompt, string? Folder, string Isolation);

public sealed record AutomationListResponse(IReadOnlyList<AutomationResponse> Automations);
