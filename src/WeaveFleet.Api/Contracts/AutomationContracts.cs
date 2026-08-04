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
    string? TargetType = null);

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
    string? TargetType = null);

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
    string TargetType);

public sealed record AutomationListResponse(IReadOnlyList<AutomationResponse> Automations);
