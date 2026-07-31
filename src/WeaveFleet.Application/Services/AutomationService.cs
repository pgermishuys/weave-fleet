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
    /// Creates a new automation with the specified configuration.
    /// Validates cron expressions for schedule-type triggers.
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
        string? agent = null)
    {
        // Validate cron expression if trigger type is schedule
        if (triggerType.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
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
        }

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
            IsEnabled = false,
            IsDeleted = false,
            WorkspaceId = workspaceId,
            Model = model,
            Agent = agent,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await automationRepository.InsertAsync(automation);
        return automation;
    }

    /// <summary>
    /// Updates an existing automation.
    /// Validates cron expressions for schedule-type triggers.
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
        string? agent = null)
    {
        var existing = await automationRepository.GetByIdAsync(id);
        if (existing is null)
            return FleetError.NotFoundFor(nameof(Automation), id);

        // Validate cron expression if trigger type is schedule
        if (triggerType.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
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
        }

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
}
