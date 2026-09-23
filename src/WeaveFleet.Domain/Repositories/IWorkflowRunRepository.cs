using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>Workflow runs and their steps. Reads by id aren't scoped to a user: load a run as its owner first.</summary>
public interface IWorkflowRunRepository
{
    Task InsertAsync(WorkflowRun run);
    Task UpdateAsync(WorkflowRun run);
    /// <summary>The run, if it's the current user's.</summary>
    Task<WorkflowRun?> GetAsync(string id);
    /// <summary>The current user's runs, newest first; only one workflow's when <paramref name="workflowId"/> is set.</summary>
    Task<IReadOnlyList<WorkflowRun>> ListAsync(int limit, string? workflowId = null);
    /// <summary>Every user's runs that are running or waiting, for picking them up after a restart.</summary>
    Task<IReadOnlyList<WorkflowRun>> ListUnfinishedAsync();

    Task InsertStepAsync(WorkflowRunStep visit);
    Task UpdateStepAsync(WorkflowRunStep visit);
    /// <summary>A run's step visits, oldest first.</summary>
    Task<IReadOnlyList<WorkflowRunStep>> ListStepsAsync(string runId);
    /// <summary>The step visit a session was started for, if any.</summary>
    Task<WorkflowRunStep?> GetStepBySessionAsync(string sessionId);
}
