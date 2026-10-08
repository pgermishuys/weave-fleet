using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>A workflow run an automation started, or why it didn't start one.</summary>
/// <param name="FirstSessionId">The run's first step session, so the automation's run has somewhere to open.</param>
/// <param name="SkipReason">Why nothing started, in words the runs list shows: "Skipped: …".</param>
public sealed record AutomationWorkflowStart(string? RunId, string? FirstSessionId, string? SkipReason)
{
    public static AutomationWorkflowStart Skipped(string reason) => new(null, null, $"Skipped: {reason}");
}

/// <summary>What automations need from workflows: starting a run, and how the last one is doing.</summary>
public interface IAutomationWorkflows
{
    /// <summary>
    /// Starts the automation's workflow with <paramref name="request"/> as its <c>{{request}}</c>, through the Run box's
    /// start path, so every check it makes still applies. Check with me is off: nobody is watching.
    /// </summary>
    Task<AutomationWorkflowStart> StartAsync(Automation automation, string request, CancellationToken ct);

    /// <summary>Whether Workflows are on for <paramref name="userId"/>.</summary>
    Task<bool> IsEnabledAsync(string userId);

    /// <summary>The run's status (<see cref="WorkflowRunStatus"/>); null when it's gone.</summary>
    Task<string?> StatusAsync(string userId, string workflowRunId);

    /// <summary>
    /// Why a new firing shouldn't start while this run is unfinished ("the last run is still waiting on you (Approve the
    /// plan)"); null when it's finished or gone.
    /// </summary>
    Task<string?> UnfinishedAsync(string userId, string workflowRunId);
}

/// <inheritdoc />
public sealed class AutomationWorkflows(
    WorkflowService workflows,
    WorkflowsFeature feature,
    IWorkflowRunRepository runs,
    IBackgroundUserScope userScope) : IAutomationWorkflows
{
    public const string TurnedOffReason = "Workflows are turned off in Settings.";

    public async Task<AutomationWorkflowStart> StartAsync(Automation automation, string request, CancellationToken ct)
    {
        // The scheduler spans users: the run, its preferences and its models are the automation owner's.
        using var owner = userScope.Begin(automation.UserId);

        if (!await feature.IsEnabledAsync().ConfigureAwait(false))
            return AutomationWorkflowStart.Skipped(TurnedOffReason);
        if (string.IsNullOrWhiteSpace(automation.WorkflowId))
            return AutomationWorkflowStart.Skipped("the automation doesn't say which workflow to run.");
        if (string.IsNullOrWhiteSpace(automation.WorkspaceId))
            return AutomationWorkflowStart.Skipped("the automation doesn't say which repository to run in.");

        // No role overrides: the roles are read from Settings → Workflows now, when it fires, not when it was saved.
        var started = await workflows.StartAsync(
            new StartWorkflowRunRequest(
                automation.WorkflowId,
                automation.WorkspaceId,
                request,
                BaseBranch: automation.BaseBranch,
                HarnessType: automation.HarnessType,
                OptionalSteps: automation.WorkflowSteps,
                RoleOverrides: null,
                CheckWithMe: false),
            new WorkflowRunStartedBy(automation.Id, automation.Name),
            ct).ConfigureAwait(false);

        return started.IsSuccess
            ? new AutomationWorkflowStart(started.Value.Id, started.Value.Sessions.Count > 0 ? started.Value.Sessions[0].SessionId : null, null)
            : AutomationWorkflowStart.Skipped(started.Error.Description);
    }

    public async Task<bool> IsEnabledAsync(string userId)
    {
        using var owner = userScope.Begin(userId);
        return await feature.IsEnabledAsync().ConfigureAwait(false);
    }

    public async Task<string?> StatusAsync(string userId, string workflowRunId)
    {
        using var owner = userScope.Begin(userId);
        return (await runs.GetAsync(workflowRunId).ConfigureAwait(false))?.Status;
    }

    public async Task<string?> UnfinishedAsync(string userId, string workflowRunId)
    {
        using var owner = userScope.Begin(userId);
        var found = await workflows.GetRunAsync(workflowRunId).ConfigureAwait(false);
        if (found.IsFailure)
            return null;

        var run = found.Value;
        return UnfinishedReason(
            run.Status,
            run.Waiting?.StepTitle,
            run.WithYou?.StepTitle,
            run.Steps.FirstOrDefault(s => s.Id == run.CurrentStepId)?.Title);
    }

    /// <summary>
    /// <see cref="UnfinishedAsync"/>'s words for a run in <paramref name="status"/>, whichever machine it's on: the step
    /// it waits on you for, the step you finish that's open, or the step running now.
    /// </summary>
    public static string? UnfinishedReason(string status, string? waitingStep, string? withYouStep, string? currentStep)
    {
        if (status == WorkflowRunStatus.Waiting)
            return waitingStep is not null ? $"the last run is still waiting on you ({waitingStep})." : "the last run is still waiting on you.";
        if (status != WorkflowRunStatus.Running)
            return null;
        if (withYouStep is not null)
            return $"the last run is still with you ({withYouStep}).";

        return currentStep is null ? "the last run is still running." : $"the last run is still running ({currentStep}).";
    }
}
