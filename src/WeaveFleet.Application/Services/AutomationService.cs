using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Encapsulates business logic for automation management.
/// </summary>
public sealed class AutomationService(
    IAutomationRepository automationRepository,
    IUserContext userContext)
{
    /// <summary>
    /// Creates a new automation with the specified configuration, switched on: the person just asked for it.
    /// Validates the schedule (a cron or a one-off time), its time zone, and where runs happen.
    /// </summary>
    public async Task<Result<Automation>> CreateAsync(
        string name,
        string prompt,
        string triggerType,
        string triggerConfig,
        int maxConcurrentRuns,
        int maxRunsPerHour,
        int timeoutMinutes,
        string? workspaceId = null,
        string? model = null,
        string? agent = null,
        List<string>? targetTags = null,
        string? targetType = null,
        string? timeZone = null,
        string? isolation = null,
        string? baseBranch = null,
        string? harnessType = null)
    {
        var error = AutomationSchedule.Validate(triggerType, triggerConfig, timeZone, DateTime.UtcNow, requireFuture: true)
            ?? ValidateWhere(workspaceId, isolation, baseBranch)
            ?? ValidateTargetType(targetType)
            ?? ValidateModel(model);
        if (error is not null)
            return error;

        var automation = new Automation
        {
            Id = Ulid.NewUlid().ToString(),
            Name = name,
            Prompt = prompt,
            TriggerType = triggerType,
            TriggerConfig = triggerConfig,
            MaxConcurrentRuns = maxConcurrentRuns,
            MaxRunsPerHour = maxRunsPerHour,
            TimeoutMinutes = timeoutMinutes,
            IsEnabled = true,
            IsDeleted = false,
            WorkspaceId = workspaceId,
            Model = NormalizeOptional(model),
            Agent = NormalizeOptional(agent),
            HarnessType = HarnessFor(model, agent, harnessType),
            TargetTags = targetTags ?? [],
            TargetType = targetType ?? "new_session",
            TimeZone = NormalizeTimeZone(timeZone),
            Isolation = NormalizeOptional(isolation),
            BaseBranch = NormalizeOptional(baseBranch),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await automationRepository.InsertAsync(automation);
        return automation;
    }

    /// <summary>
    /// Updates an existing automation.
    /// Validates the schedule (a cron or a one-off time), its time zone, and where runs happen.
    /// </summary>
    public async Task<Result<Automation>> UpdateAsync(
        string id,
        string name,
        string prompt,
        string triggerType,
        string triggerConfig,
        int maxConcurrentRuns,
        int maxRunsPerHour,
        int timeoutMinutes,
        string? workspaceId = null,
        string? model = null,
        string? agent = null,
        List<string>? targetTags = null,
        string? targetType = null,
        string? timeZone = null,
        string? isolation = null,
        string? baseBranch = null,
        string? harnessType = null)
    {
        var existing = await automationRepository.GetByIdAsync(id);
        if (existing is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        // A one-off time that has passed is fine on an automation that's off (it already ran); not on one that's on.
        var error = AutomationSchedule.Validate(triggerType, triggerConfig, timeZone, DateTime.UtcNow, requireFuture: existing.IsEnabled)
            ?? ValidateWhere(workspaceId, isolation, baseBranch)
            ?? ValidateTargetType(targetType)
            ?? ValidateModel(model);
        if (error is not null)
            return error;

        existing.Name = name;
        existing.Prompt = prompt;
        existing.TriggerType = triggerType;
        existing.TriggerConfig = triggerConfig;
        existing.MaxConcurrentRuns = maxConcurrentRuns;
        existing.MaxRunsPerHour = maxRunsPerHour;
        existing.TimeoutMinutes = timeoutMinutes;
        existing.WorkspaceId = workspaceId;
        existing.Model = NormalizeOptional(model);
        existing.Agent = NormalizeOptional(agent);
        existing.HarnessType = HarnessFor(model, agent, harnessType);
        existing.TargetTags = targetTags ?? [];
        existing.TargetType = targetType ?? "new_session";
        existing.TimeZone = NormalizeTimeZone(timeZone);
        existing.Isolation = NormalizeOptional(isolation);
        existing.BaseBranch = NormalizeOptional(baseBranch);
        existing.UpdatedAt = DateTime.UtcNow.ToString("O");

        await automationRepository.UpdateAsync(existing);
        return existing;
    }

    /// <summary>
    /// Enables an automation.
    /// </summary>
    public async Task<Result<Unit>> EnableAsync(string id)
    {
        var automation = await automationRepository.GetByIdAsync(id);
        if (automation is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        if (AutomationSchedule.IsOnce(automation.TriggerType)
            && AutomationSchedule.OnceUtc(automation.TriggerConfig, automation.TimeZone) is { } at
            && at <= DateTime.UtcNow)
        {
            return FleetError.ValidationError("TriggerConfig", "Its one-off time has passed. Pick a new time, then switch it on.");
        }

        await automationRepository.SetEnabledAsync(id, true);
        return Unit.Value;
    }

    /// <summary>
    /// Disables an automation.
    /// </summary>
    public async Task<Result<Unit>> DisableAsync(string id)
    {
        var automation = await automationRepository.GetByIdAsync(id);
        if (automation is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        await automationRepository.SetEnabledAsync(id, false);
        return Unit.Value;
    }

    /// <summary>
    /// Soft-deletes an automation by marking it as deleted.
    /// </summary>
    public async Task<Result<Unit>> DeleteAsync(string id)
    {
        var automation = await automationRepository.GetByIdAsync(id);
        if (automation is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        await automationRepository.DeleteAsync(id);
        return Unit.Value;
    }

    /// <summary>
    /// Retrieves an automation by ID.
    /// </summary>
    public async Task<Result<Automation>> GetByIdAsync(string id)
    {
        var automation = await automationRepository.GetByIdAsync(id);
        if (automation is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        return automation;
    }

    /// <summary>
    /// Lists all automations, optionally filtered by workspace.
    /// </summary>
    public async Task<Result<IReadOnlyList<Automation>>> ListAsync(string? workspaceId = null)
    {
        var automations = await automationRepository.ListAsync(workspaceId);
        return Result.Success(automations);
    }

    /// <summary>
    /// Loads an automation for manual execution.
    /// Returns the automation entity for the caller to execute.
    /// </summary>
    public async Task<Result<Automation>> TriggerManuallyAsync(string id)
    {
        var automation = await automationRepository.GetByIdAsync(id);
        if (automation is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        return automation;
    }

    /// <summary>
    /// Where a run happens: a worktree needs a folder, and only a worktree has a base branch. No isolation (automations
    /// made before it was stored) keeps the old rules.
    /// </summary>
    private static FleetError? ValidateWhere(string? workspaceId, string? isolation, string? baseBranch)
    {
        var mode = NormalizeOptional(isolation);
        if (mode is not (null or "worktree" or "existing"))
            return FleetError.ValidationError("Isolation", $"Runs can happen in a new worktree ('worktree') or the folder as it is ('existing'), not '{mode}'.");

        if (mode == "worktree" && string.IsNullOrWhiteSpace(workspaceId))
            return FleetError.ValidationError("Isolation", "A new worktree for each run needs a folder. Pick one, or run in the folder as it is.");

        var branch = NormalizeOptional(baseBranch);
        if (branch is null)
            return null;

        if (mode != "worktree")
            return FleetError.ValidationError("BaseBranch", "A base branch can only be chosen when each run gets a new worktree.");

        return WorkspaceService.IsValidBranchName(branch)
            ? null
            : FleetError.ValidationError("BaseBranch", $"'{branch}' is not a valid branch name.");
    }

    private static readonly string[] TargetTypes = ["new_session", "same_session", "most_recent_session", "tagged_session"];

    private static FleetError? ValidateTargetType(string? targetType) =>
        targetType is null || TargetTypes.Contains(targetType, StringComparer.Ordinal)
            ? null
            : FleetError.ValidationError("TargetType", $"Unknown target '{targetType}'. Use one of: {string.Join(", ", TargetTypes)}.");

    /// <summary>A model is <c>provider/model</c>, split at the first slash: the model part may have slashes of its own.</summary>
    private static FleetError? ValidateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var slash = model.Trim().IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < model.Trim().Length - 1
            ? null
            : FleetError.ValidationError("Model", $"Model '{model}' must be written provider/model, e.g. anthropic/claude-sonnet-4-5.");
    }

    /// <summary>
    /// An agent or model only means something on the harness it was picked from, so the automation keeps that
    /// harness with them. Without either it follows the default harness, as automations always have.
    /// </summary>
    private static string? HarnessFor(string? model, string? agent, string? harnessType) =>
        string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(agent) ? null : NormalizeOptional(harnessType);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeTimeZone(string? timeZone) =>
        string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
}
