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
    /// <summary>"Check with me after each step": steps that start from now on are ones you finish.</summary>
    public bool CheckWithMe { get; init; }
    /// <summary>The step you finish that's open now; null when there's none.</summary>
    public WorkflowRunWithYouDto? WithYou { get; init; }
}

/// <summary>A step of the run, in the workflow's order.</summary>
/// <param name="Kind"><c>agent</c> or <c>you</c>.</param>
/// <param name="State"><c>pending</c>, <c>running</c>, <c>waiting</c>, <c>done</c> or <c>skipped</c>.</param>
/// <param name="Visits">How many times the step has run in this run.</param>
/// <param name="Model">The model it runs on, <c>provider/model</c>; null for the default model.</param>
/// <param name="Role">The role it asked for, when it named one.</param>
/// <param name="MaxLoops">How many times it may send work back.</param>
/// <param name="FinishYou">The workflow file says <c>finish: you</c>.</param>
/// <param name="FinishAgent">The workflow file says <c>finish: agent</c>: Check with me doesn't change it.</param>
/// <param name="WithYou">
/// You finish it: for a step that ran, how its last visit started; for one still to come, what it will be as things
/// stand (<c>finish: you</c>, or Check with me on for a step whose file doesn't say).
/// </param>
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
    int? MaxLoops,
    bool FinishYou,
    bool FinishAgent,
    bool WithYou,
    IReadOnlyList<string> Outcomes);

/// <param name="Visit">1 for the step's first run, 2 for the second pass, and so on.</param>
/// <param name="Summary">What the agent passed to <c>fleet_step_done</c>, or the reply to a wrap-up.</param>
/// <param name="WithYou">You finish this visit.</param>
/// <param name="Files">The files the step declares, with the run filled in.</param>
/// <param name="FilesChecked">They were all there before the next step started.</param>
/// <param name="FilesCommit">The short SHA of the commit Fleet made of them then; null when there was nothing to commit.</param>
/// <param name="FilesCommitError">Why Fleet couldn't commit them; the run went on.</param>
/// <param name="PromptMessageId">The prompt the session started with.</param>
/// <param name="WrapUpMessageId">The wrap-up prompt Fleet sent when you moved on.</param>
/// <param name="HandOffNote">Your note for the next step.</param>
public sealed record WorkflowRunSessionDto(
    string SessionId,
    string StepId,
    int Visit,
    string? Outcome,
    string? Summary,
    string? Note,
    bool WithYou,
    IReadOnlyList<string> Files,
    bool FilesChecked,
    string? FilesCommit,
    string? FilesCommitError,
    string? PromptMessageId,
    string? WrapUpMessageId,
    string? HandOffNote);

/// <summary>A step you finish, open now: the Move on bar above the composer.</summary>
/// <param name="WrappingUp">You moved on; the agent is bringing the files up to date.</param>
/// <param name="Moves">One per outcome: where it goes.</param>
public sealed record WorkflowRunWithYouDto(
    string StepId,
    string StepTitle,
    string SessionId,
    IReadOnlyList<string> Files,
    bool WrappingUp,
    string? Outcome,
    IReadOnlyList<WorkflowRunMoveDto> Moves);

/// <summary>A way to move on from a step you finish.</summary>
/// <param name="To">The step it leads to, past optional steps that are off; null for the end of the run.</param>
/// <param name="Back">It sends the work back to an earlier step.</param>
/// <param name="LoopsUsed">How many times the step has sent work back this way; null when it doesn't loop.</param>
/// <param name="Allowed">False when the loop's maximum is used up.</param>
public sealed record WorkflowRunMoveDto(string Outcome, string? To, string? ToTitle, bool Back, int? LoopsUsed, int? MaxLoops, bool Allowed);

/// <summary>What the run waits on you for, and what you can do.</summary>
/// <param name="Kind">
/// <c>you</c> (a You step), <c>no-outcome</c> (a step stopped without calling the tool), <c>loop-limit</c> (a step
/// sent work back more times than it may) or <c>start-failed</c> (Fleet couldn't start a step).
/// </param>
/// <param name="SessionId">The session the card shows in: the step's own, or the last step's before a You step.</param>
/// <param name="Question">What a You step asks.</param>
/// <param name="Files">
/// For a You step, the files the step before it declares, which open next to the card; for missing files, the files
/// the step declares (the message names the ones that are missing).
/// </param>
/// <param name="ThenAlone">
/// For a You step's choice that leads on to more than one agent step: the agent steps that run before the next You
/// step, so the card can say what "let it run" means. Empty otherwise.
/// </param>
/// <param name="NextYouTitle">The You step after those, when there is one.</param>
public sealed record WorkflowRunWaitingDto(
    string Kind,
    string StepId,
    string StepTitle,
    string Message,
    string? Question,
    string? SessionId,
    IReadOnlyList<WorkflowRunChoiceDto> Choices,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ThenAlone,
    string? NextYouTitle);

/// <param name="Id">
/// What to send back to answer: <c>choice:&lt;n&gt;</c>, <c>outcome:&lt;name&gt;</c>, <c>retry</c> or
/// <c>move-on-anyway</c>.
/// </param>
/// <param name="Note">The choice asks for a note.</param>
/// <param name="To">The step it leads to, or <c>end</c>.</param>
/// <param name="Involvement">It goes on to two or more agent steps, so it's offered two ways: let it run, or check with you.</param>
public sealed record WorkflowRunChoiceDto(string Id, string Label, bool Note, string? To, bool Involvement = false);

public static class WorkflowWaitingKinds
{
    public const string You = "you";
    public const string NoOutcome = "no-outcome";
    public const string LoopLimit = "loop-limit";
    public const string StartFailed = "start-failed";
    /// <summary>A file a step declares isn't in the run's worktree.</summary>
    public const string MissingFiles = "missing-files";
    /// <summary>The wrap-up turn of a step you finish failed, or a restart cut it off.</summary>
    public const string WrapUpFailed = "wrap-up-failed";
}

/// <summary>Builds <see cref="WorkflowRunDto"/> from a run's rows.</summary>
public static class WorkflowRunView
{
    public static WorkflowRunDto Build(WorkflowRun run, WorkflowDefinition? workflow, IReadOnlyList<WorkflowRunStep> visits)
    {
        var options = WorkflowRunOptions.Read(run.Options);
        var state = workflow is null ? null : new WorkflowRunner.RunState(run, workflow, options, visits.ToList());
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
                agent?.MaxLoops,
                agent?.FinishYou ?? false,
                agent?.FinishAgent ?? false,
                agent is not null && (last?.Finish is { } finish
                    ? finish == WorkflowFinishers.You
                    : agent.UserFinishes(options.CheckWithMe)),
                agent?.Outcomes ?? []));
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
                .Select(v => new WorkflowRunSessionDto(
                    v.SessionId!,
                    v.StepId,
                    v.Visit,
                    v.Outcome,
                    v.Summary,
                    v.Note,
                    v.Finish == WorkflowFinishers.You,
                    state is not null && workflow!.Find(v.StepId) is WorkflowAgentStep step ? WorkflowRunner.FilesOf(state, step) : [],
                    v.FilesChecked,
                    v.FilesCommit,
                    v.FilesCommitError,
                    v.PromptMessageId,
                    v.WrapUpMessageId,
                    v.HandOffNote))
                .ToList(),
            Waiting = run.Status == WorkflowRunStatus.Waiting && state is not null ? Waiting(state) : null,
            CheckWithMe = options.CheckWithMe,
            WithYou = run.Status == WorkflowRunStatus.Running && state is not null ? WithYou(state) : null,
        };
    }

    /// <summary>The step you finish that's open: its Move on choices, or that it's wrapping up.</summary>
    private static WorkflowRunWithYouDto? WithYou(WorkflowRunner.RunState state)
    {
        if (state.Current is not { Finish: WorkflowFinishers.You, SessionId: { } sessionId } visit
            || visit.Status is not (WorkflowRunStepStatus.Running or WorkflowRunStepStatus.WrappingUp)
            || state.Workflow.Find(visit.StepId) is not WorkflowAgentStep step)
        {
            return null;
        }

        var moves = step.Outcomes.Select(outcome =>
        {
            var target = step.Routes.GetValueOrDefault(outcome) ?? NextOf(state.Workflow, step.Id);
            var loops = WorkflowRunner.LoopsLeft(state, step, outcome);
            return new WorkflowRunMoveDto(
                outcome,
                target == WorkflowTargets.End ? null : target,
                WorkflowRunner.NextTitle(state, step, outcome),
                target != WorkflowTargets.End && state.Workflow.IndexOf(target) <= state.Workflow.IndexOf(step.Id),
                loops?.Used,
                loops?.Max,
                loops is not { Left: <= 0 });
        }).ToList();

        return new WorkflowRunWithYouDto(
            step.Id,
            step.Title,
            sessionId,
            WorkflowRunner.FilesOf(state, step),
            visit.Status == WorkflowRunStepStatus.WrappingUp,
            visit.Status == WorkflowRunStepStatus.WrappingUp ? visit.Outcome : null,
            moves);
    }

    private static string NextOf(WorkflowDefinition workflow, string stepId)
    {
        var index = workflow.IndexOf(stepId);
        return index >= 0 && index + 1 < workflow.Steps.Count ? workflow.Steps[index + 1].Id : WorkflowTargets.End;
    }

    private static string StateOf(WorkflowRunStep? last, WorkflowRun run) => last?.Status switch
    {
        null => "pending",
        WorkflowRunStepStatus.Running => WorkflowRunStatus.IsFinished(run.Status) ? "done" : "running",
        WorkflowRunStepStatus.Waiting => WorkflowRunStatus.IsFinished(run.Status) ? "done" : "waiting",
        WorkflowRunStepStatus.WrappingUp => WorkflowRunStatus.IsFinished(run.Status) ? "done"
            : run.Status == WorkflowRunStatus.Waiting ? "waiting" : "running",
        WorkflowRunStepStatus.Skipped => "skipped",
        // A step past its loop limit is done but the run waits on it.
        _ when run.Status == WorkflowRunStatus.Waiting && run.CurrentStepId == last.StepId => "waiting",
        _ => "done",
    };

    /// <summary>The card for a waiting run, from its current visit.</summary>
    internal static WorkflowRunWaitingDto? Waiting(WorkflowRunner.RunState state)
    {
        var (run, workflow, visits) = (state.Run, state.Workflow, state.Visits);
        var visit = visits.Count > 0 ? visits[^1] : null;
        if (visit is null || workflow.Find(visit.StepId) is not { } step)
            return null;

        var message = run.WaitingReason ?? string.Empty;
        switch (step)
        {
            case WorkflowYouStep you:
            {
                // What "let it run" would run: the agent steps up to the next You step.
                var ahead = new List<string>();
                string? nextYou = null;
                for (var i = workflow.IndexOf(you.Id) + 1; i < workflow.Steps.Count; i++)
                {
                    if (workflow.Steps[i] is WorkflowYouStep later)
                    {
                        nextYou = later.Title;
                        break;
                    }

                    if (workflow.Steps[i] is WorkflowAgentStep agent && (!agent.Optional || state.Options.OptionalSteps.Contains(agent.Id)))
                        ahead.Add(agent.Title);
                }

                var involvement = ahead.Count >= 2;
                var before = visits.Take(visits.Count - 1).LastOrDefault(v => v.SessionId is not null);
                return new WorkflowRunWaitingDto(
                    WorkflowWaitingKinds.You,
                    you.Id,
                    you.Title,
                    message,
                    you.Ask,
                    LastSessionBefore(visits, visits.Count - 1),
                    you.Choices.Select((c, i) => new WorkflowRunChoiceDto(
                        $"choice:{i}", c.Label, c.Note, c.To,
                        involvement && !c.Note && c.To != WorkflowTargets.End && workflow.IndexOf(c.To) > workflow.IndexOf(you.Id))).ToList(),
                    before is not null && workflow.Find(before.StepId) is WorkflowAgentStep beforeStep ? WorkflowRunner.FilesOf(state, beforeStep) : [],
                    involvement ? ahead : [],
                    involvement ? nextYou : null);
            }

            case WorkflowAgentStep agent when run.WaitingKind is WorkflowWaitingKinds.MissingFiles or WorkflowWaitingKinds.WrapUpFailed:
                return new WorkflowRunWaitingDto(
                    run.WaitingKind,
                    agent.Id,
                    agent.Title,
                    message,
                    null,
                    visit.SessionId,
                    [new WorkflowRunChoiceDto(WorkflowRunner.MoveOnAnywayChoice, "Move on anyway", false, null)],
                    run.WaitingKind == WorkflowWaitingKinds.MissingFiles ? WorkflowRunner.FilesOf(state, agent) : [],
                    [],
                    null);

            case WorkflowAgentStep agent when visit.SessionId is null:
                return new WorkflowRunWaitingDto(
                    WorkflowWaitingKinds.StartFailed,
                    agent.Id,
                    agent.Title,
                    message,
                    null,
                    LastSessionBefore(visits, visits.Count - 1),
                    [new WorkflowRunChoiceDto("retry", "Try again", false, agent.Id)],
                    [],
                    [],
                    null);

            case WorkflowAgentStep agent:
                var kind = visit.Status == WorkflowRunStepStatus.Done ? WorkflowWaitingKinds.LoopLimit : WorkflowWaitingKinds.NoOutcome;
                return new WorkflowRunWaitingDto(
                    kind,
                    agent.Id,
                    agent.Title,
                    message,
                    null,
                    visit.SessionId,
                    agent.Outcomes.Select(o => new WorkflowRunChoiceDto($"outcome:{o}", o, false, agent.Routes.GetValueOrDefault(o))).ToList(),
                    [],
                    [],
                    null);

            default:
                return null;
        }
    }

    private static string? LastSessionBefore(List<WorkflowRunStep> visits, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (visits[i].SessionId is { } session)
                return session;
        }

        return null;
    }
}
