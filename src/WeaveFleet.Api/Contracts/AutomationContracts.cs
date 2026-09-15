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
    /// <summary>The harness <c>Model</c> and <c>Agent</c> were picked from; ignored without either.</summary>
    string? HarnessType = null);

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
    /// <summary>The harness <c>Model</c> and <c>Agent</c> were picked from; ignored without either.</summary>
    string? HarnessType = null);

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
    /// <summary>When it runs next (UTC, ISO 8601); null when it's off or waits for an event.</summary>
    string? NextRunAt,
    AutomationRunResponse? LastRun);

/// <summary>One run. State is "starting", "running", "done", "failed" or "skipped".</summary>
public sealed record AutomationRunResponse(
    string Id,
    string AutomationId,
    string Trigger,
    string? ScheduledFor,
    string StartedAt,
    string State,
    string? SessionId,
    string? InstanceId,
    string? Error);

public sealed record AutomationRunListResponse(IReadOnlyList<AutomationRunResponse> Runs);

/// <summary>A new automation's starting point from a session: its first message and where it ran.</summary>
public sealed record AutomationDraftResponse(string Prompt, string? Folder, string Isolation);

public sealed record AutomationListResponse(IReadOnlyList<AutomationResponse> Automations);
