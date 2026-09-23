using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// Workflow runs in memory. It keeps copies, so a change the runner makes to a run or a visit only lands when it's
/// saved, as it would in the database.
/// </summary>
public sealed class InMemoryWorkflowRunRepository : IWorkflowRunRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);
    private readonly List<WorkflowRunStep> _steps = [];

    public IReadOnlyList<WorkflowRunStep> Steps
    {
        get
        {
            lock (_gate)
                return _steps.Select(Copy).ToList();
        }
    }

    public WorkflowRun Run(string id)
    {
        lock (_gate)
            return Copy(_runs[id]);
    }

    public Task InsertAsync(WorkflowRun run)
    {
        lock (_gate)
            _runs[run.Id] = Copy(run);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(WorkflowRun run)
    {
        lock (_gate)
            _runs[run.Id] = Copy(run);
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetAsync(string id)
    {
        lock (_gate)
            return Task.FromResult(_runs.TryGetValue(id, out var run) ? Copy(run) : null);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListAsync(int limit, string? workflowId = null)
    {
        lock (_gate)
        {
            IReadOnlyList<WorkflowRun> list = _runs.Values
                .Where(run => workflowId is null || run.WorkflowId == workflowId)
                .OrderByDescending(run => run.CreatedAt)
                .Take(limit)
                .Select(Copy)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<WorkflowRun>> ListUnfinishedAsync()
    {
        lock (_gate)
        {
            IReadOnlyList<WorkflowRun> list = _runs.Values
                .Where(run => run.Status is WorkflowRunStatus.Running or WorkflowRunStatus.Waiting)
                .Select(Copy)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task InsertStepAsync(WorkflowRunStep visit)
    {
        lock (_gate)
            _steps.Add(Copy(visit));
        return Task.CompletedTask;
    }

    public Task UpdateStepAsync(WorkflowRunStep visit)
    {
        lock (_gate)
        {
            var index = _steps.FindIndex(s => s.Id == visit.Id);
            if (index >= 0)
                _steps[index] = Copy(visit);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowRunStep>> ListStepsAsync(string runId)
    {
        lock (_gate)
        {
            IReadOnlyList<WorkflowRunStep> list = _steps.Where(s => s.RunId == runId).Select(Copy).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<WorkflowRunStep?> GetStepBySessionAsync(string sessionId)
    {
        lock (_gate)
            return Task.FromResult(_steps.LastOrDefault(s => s.SessionId == sessionId) is { } step ? Copy(step) : null);
    }

    private static WorkflowRun Copy(WorkflowRun run) => new()
    {
        Id = run.Id,
        UserId = run.UserId,
        WorkflowId = run.WorkflowId,
        WorkflowName = run.WorkflowName,
        Definition = run.Definition,
        Request = run.Request,
        Slug = run.Slug,
        Title = run.Title,
        RepositoryPath = run.RepositoryPath,
        BaseBranch = run.BaseBranch,
        Branch = run.Branch,
        WorktreePath = run.WorktreePath,
        HarnessType = run.HarnessType,
        HarnessProfileId = run.HarnessProfileId,
        Options = run.Options,
        Status = run.Status,
        CurrentStepId = run.CurrentStepId,
        WaitingReason = run.WaitingReason,
        Result = run.Result,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
        EndedAt = run.EndedAt,
    };

    private static WorkflowRunStep Copy(WorkflowRunStep step) => new()
    {
        Id = step.Id,
        RunId = step.RunId,
        StepId = step.StepId,
        Visit = step.Visit,
        SessionId = step.SessionId,
        Status = step.Status,
        Outcome = step.Outcome,
        Summary = step.Summary,
        Note = step.Note,
        StartedAt = step.StartedAt,
        FinishedAt = step.FinishedAt,
    };
}
