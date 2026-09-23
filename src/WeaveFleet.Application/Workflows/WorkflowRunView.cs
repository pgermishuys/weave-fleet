using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Workflows;

/// <summary>A run as the client shows it: the group in Sessions, the stepper, and the card when it waits on you.</summary>
public sealed record WorkflowRunDto
{
    public required string Id { get; init; }
    public required string WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string Title { get; init; }
    public required string Request { get; init; }
    /// <summary><c>running</c>, <c>waiting</c>, <c>done</c>, <c>ended</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }
    public string? CurrentStepId { get; init; }
    public string? Result { get; init; }
    public required string RepositoryPath { get; init; }
    public string? Branch { get; init; }
    public string? BaseBranch { get; init; }
    public string? WorktreePath { get; init; }
    public required string HarnessType { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public string? EndedAt { get; init; }
    public required IReadOnlyList<WorkflowRunStepDto> Steps { get; init; }
    /// <summary>Every step session of the run, oldest first, with the step each was for.</summary>
    public required IReadOnlyList<WorkflowRunSessionDto> Sessions { get; init; }
    /// <summary>What the run is waiting on you for; null unless <see cref="Status"/> is <c>waiting</c>.</summary>
    public WorkflowRunWaitingDto? Waiting { get; init; }
}

/// <summary>A step of the run, in the workflow's order.</summary>
/// <param name="Kind"><c>agent</c> or <c>you</c>.</param>
/// <param name="State"><c>pending</c>, <c>running</c>, <c>waiting</c>, <c>done</c> or <c>skipped</c>.</param>
/// <param name="Visits">How many times the step has run in this run.</param>
/// <param name="Model">The model it runs on, <c>provider/model</c>; null for the default model.</param>
/// <param name="Role">The role it asked for, when it named one.</param>
/// <param name="MaxLoops">How many times it may send work back.</param>
public sealed record WorkflowRunStepDto(
    string Id,
    string Title,
    string Kind,
    string State,
    int Visits,
    bool Optional,
    bool Enabled,
    string? Outcome,
    string? SessionId,
    string? Model,
    string? Role,
    string? Skill,
    int? MaxLoops);

/// <param name="Visit">1 for the step's first run, 2 for the second pass, and so on.</param>
/// <param name="Summary">What the agent passed to <c>fleet_step_done</c>.</param>
public sealed record WorkflowRunSessionDto(string SessionId, string StepId, int Visit, string? Outcome, string? Summary, string? Note);

/// <summary>What the run waits on you for, and what you can do.</summary>
/// <param name="Kind">
/// <c>you</c> (a You step), <c>no-outcome</c> (a step stopped without calling the tool), <c>loop-limit</c> (a step
/// sent work back more times than it may) or <c>start-failed</c> (Fleet couldn't start a step).
/// </param>
/// <param name="SessionId">The session the card shows in: the step's own, or the last step's before a You step.</param>
/// <param name="Question">What a You step asks.</param>
public sealed record WorkflowRunWaitingDto(
    string Kind,
    string StepId,
    string StepTitle,
    string Message,
    string? Question,
    string? SessionId,
    IReadOnlyList<WorkflowRunChoiceDto> Choices);

/// <param name="Id">What to send back to answer: <c>choice:&lt;n&gt;</c>, <c>outcome:&lt;name&gt;</c> or <c>retry</c>.</param>
/// <param name="Note">The choice asks for a note.</param>
/// <param name="To">The step it leads to, or <c>end</c>.</param>
public sealed record WorkflowRunChoiceDto(string Id, string Label, bool Note, string? To);

public static class WorkflowWaitingKinds
{
    public const string You = "you";
    public const string NoOutcome = "no-outcome";
    public const string LoopLimit = "loop-limit";
    public const string StartFailed = "start-failed";
}

/// <summary>Builds <see cref="WorkflowRunDto"/> from a run's rows.</summary>
public static class WorkflowRunView
{
    public static WorkflowRunDto Build(WorkflowRun run, WorkflowDefinition? workflow, IReadOnlyList<WorkflowRunStep> visits)
    {
        var options = WorkflowRunOptions.Read(run.Options);
        var steps = new List<WorkflowRunStepDto>();
        foreach (var step in workflow?.Steps ?? [])
        {
            var mine = visits.Where(v => v.StepId == step.Id).ToList();
            var last = mine.LastOrDefault();
            var agent = step as WorkflowAgentStep;
            var enabled = agent is not { Optional: true } || options.OptionalSteps.Contains(step.Id);
            var model = agent is not null && options.StepModels.TryGetValue(step.Id, out var chosen) ? chosen.Model : null;
            steps.Add(new WorkflowRunStepDto(
                step.Id,
                step.Title,
                agent is null ? "you" : "agent",
                StateOf(last, run),
                mine.Count(v => v.Status != WorkflowRunStepStatus.Skipped),
                agent?.Optional ?? false,
                enabled,
                last?.Outcome,
                mine.LastOrDefault(v => v.SessionId is not null)?.SessionId,
                model,
                agent is not null && WorkflowRoles.IsRole(agent.Model) ? agent.Model : null,
                agent?.Skill,
                agent?.MaxLoops));
        }

        return new WorkflowRunDto
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowName = run.WorkflowName,
            Title = run.Title,
            Request = run.Request,
            Status = run.Status,
            CurrentStepId = run.CurrentStepId,
            Result = run.Result,
            RepositoryPath = run.RepositoryPath,
            Branch = run.Branch,
            BaseBranch = run.BaseBranch,
            WorktreePath = run.WorktreePath,
            HarnessType = run.HarnessType,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            EndedAt = run.EndedAt,
            Steps = steps,
            Sessions = visits
                .Where(v => v.SessionId is not null)
                .Select(v => new WorkflowRunSessionDto(v.SessionId!, v.StepId, v.Visit, v.Outcome, v.Summary, v.Note))
                .ToList(),
            Waiting = run.Status == WorkflowRunStatus.Waiting && workflow is not null ? Waiting(run, workflow, visits) : null,
        };
    }

    private static string StateOf(WorkflowRunStep? last, WorkflowRun run) => last?.Status switch
    {
        null => "pending",
        WorkflowRunStepStatus.Running => WorkflowRunStatus.IsFinished(run.Status) ? "done" : "running",
        WorkflowRunStepStatus.Waiting => WorkflowRunStatus.IsFinished(run.Status) ? "done" : "waiting",
        WorkflowRunStepStatus.Skipped => "skipped",
        // A step past its loop limit is done but the run waits on it.
        _ when run.Status == WorkflowRunStatus.Waiting && run.CurrentStepId == last.StepId => "waiting",
        _ => "done",
    };

    /// <summary>The card for a waiting run, from its current visit.</summary>
    internal static WorkflowRunWaitingDto? Waiting(WorkflowRun run, WorkflowDefinition workflow, IReadOnlyList<WorkflowRunStep> visits)
    {
        var visit = visits.Count > 0 ? visits[^1] : null;
        if (visit is null || workflow.Find(visit.StepId) is not { } step)
            return null;

        var message = run.WaitingReason ?? string.Empty;
        switch (step)
        {
            case WorkflowYouStep you:
                return new WorkflowRunWaitingDto(
                    WorkflowWaitingKinds.You,
                    you.Id,
                    you.Title,
                    message,
                    you.Ask,
                    LastSessionBefore(visits, visits.Count - 1),
                    you.Choices.Select((c, i) => new WorkflowRunChoiceDto($"choice:{i}", c.Label, c.Note, c.To)).ToList());

            case WorkflowAgentStep agent when visit.SessionId is null:
                return new WorkflowRunWaitingDto(
                    WorkflowWaitingKinds.StartFailed,
                    agent.Id,
                    agent.Title,
                    message,
                    null,
                    LastSessionBefore(visits, visits.Count - 1),
                    [new WorkflowRunChoiceDto("retry", "Try again", false, agent.Id)]);

            case WorkflowAgentStep agent:
                var kind = visit.Status == WorkflowRunStepStatus.Done ? WorkflowWaitingKinds.LoopLimit : WorkflowWaitingKinds.NoOutcome;
                return new WorkflowRunWaitingDto(
                    kind,
                    agent.Id,
                    agent.Title,
                    message,
                    null,
                    visit.SessionId,
                    agent.Outcomes.Select(o => new WorkflowRunChoiceDto($"outcome:{o}", o, false, agent.Routes.GetValueOrDefault(o))).ToList());

            default:
                return null;
        }
    }

    private static string? LastSessionBefore(IReadOnlyList<WorkflowRunStep> visits, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (visits[i].SessionId is { } session)
                return session;
        }

        return null;
    }
}
