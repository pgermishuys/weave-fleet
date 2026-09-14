using Cronos;
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
    /// Validates cron expressions and time zones for schedule-type triggers.
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
        string? timeZone = null)
    {
        var scheduleError = ValidateSchedule(triggerType, triggerConfig, timeZone);
        if (scheduleError is not null)
            return scheduleError;

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
            Model = model,
            Agent = agent,
            TargetTags = targetTags ?? [],
            TargetType = targetType ?? "new_session",
            TimeZone = NormalizeTimeZone(timeZone),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await automationRepository.InsertAsync(automation);
        return automation;
    }

    /// <summary>
    /// Updates an existing automation.
    /// Validates cron expressions and time zones for schedule-type triggers.
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
        string? timeZone = null)
    {
        var existing = await automationRepository.GetByIdAsync(id);
        if (existing is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        var scheduleError = ValidateSchedule(triggerType, triggerConfig, timeZone);
        if (scheduleError is not null)
            return scheduleError;

        existing.Name = name;
        existing.Prompt = prompt;
        existing.TriggerType = triggerType;
        existing.TriggerConfig = triggerConfig;
        existing.MaxConcurrentRuns = maxConcurrentRuns;
        existing.MaxRunsPerHour = maxRunsPerHour;
        existing.TimeoutMinutes = timeoutMinutes;
        existing.WorkspaceId = workspaceId;
        existing.Model = model;
        existing.Agent = agent;
        existing.TargetTags = targetTags ?? [];
        existing.TargetType = targetType ?? "new_session";
        existing.TimeZone = NormalizeTimeZone(timeZone);
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

    private static FleetError? ValidateSchedule(string triggerType, string triggerConfig, string? timeZone)
    {
        if (!triggerType.Equals("schedule", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            CronExpression.Parse(triggerConfig);
        }
        catch (Exception ex)
        {
            return FleetError.ValidationError(
                "TriggerConfig",
                $"Invalid cron expression: {ex.Message}");
        }

        var zone = NormalizeTimeZone(timeZone);
        if (zone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            return FleetError.ValidationError(
                "TimeZone",
                $"Unknown time zone '{zone}'. Use an IANA name such as 'Europe/London'.");
        }

        return null;
    }

    private static string? NormalizeTimeZone(string? timeZone) =>
        string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
}
