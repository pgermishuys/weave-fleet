using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>Starts the session for an agent step and sends it the step's prompt.</summary>
public interface IWorkflowStepSessions
{
    Task<WorkflowStepSession> StartAsync(WorkflowRun run, WorkflowAgentStep agentStep, string prompt, WorkflowModelChoice model, CancellationToken ct);
}

/// <summary>The session a step started in, or why it couldn't start.</summary>
/// <param name="Directory">Where the session works: the run's worktree.</param>
public sealed record WorkflowStepSession(string? SessionId, string? Directory, string? Branch, string? Error)
{
    public static WorkflowStepSession Failed(string error, string? sessionId = null) => new(sessionId, null, null, error);
}

/// <summary>Tells the client a run changed, and the user when it needs them.</summary>
public interface IWorkflowRunEvents
{
    Task ChangedAsync(WorkflowRunDto run, string userId);
    Task NeedsYouAsync(WorkflowRunDto run, string userId);
}

/// <summary>How a <c>fleet_step_done</c> call went, in words the agent reads.</summary>
public sealed record WorkflowStepDoneResult(bool Accepted, string Message);

/// <summary>
/// Moves workflow runs from step to step. A step ends when its session calls <see cref="Workflows.StepTool"/>; the
/// outcome picks the next step. Fleet never advances on idle alone: a step whose turn ends without the tool waits on
/// the user, and so does a You step and a loop past its maximum. Fleet never prompts an agent on its own.
/// </summary>
/// <remarks>
/// Every change to a run happens under that run's lock and is written before the next thing starts, so a restart
/// finds the run where it was (<see cref="RecoverAsync"/>).
/// </remarks>
public sealed partial class WorkflowRunner(
    IServiceScopeFactory scopeFactory,
    SessionActivityTracker activity,
    TimeProvider time,
    ILogger<WorkflowRunner> logger)
{
    /// <summary>How long after a step's session goes idle Fleet looks again before it decides the step stopped.</summary>
    internal TimeSpan IdleGrace { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StepWatch> _watches = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<Task> _inFlight = [];

    /// <summary>Finished when every stall check in flight is done. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_inFlight);
        }
    }

    // ── Starting ───────────────────────────────────────────────────────────────

    /// <summary>Saves a run the caller built and checked, and starts its first step.</summary>
    public async Task<WorkflowRunDto> StartAsync(WorkflowRun run, CancellationToken ct = default)
    {
        using var scope = Begin(run.UserId);
        await scope.Runs.InsertAsync(run).ConfigureAwait(false);
        LogStarted(run.Id, run.WorkflowId);

        return await WithRunAsync(scope, run.Id, async state =>
        {
            await GoToAsync(scope, state, state.Workflow.Steps[0].Id, note: null, ct).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false) ?? throw new InvalidOperationException("The run just saved can't be read back.");
    }

    // ── fleet_step_done ────────────────────────────────────────────────────────

    /// <summary>
    /// A step's session says it's finished. The caller has checked the call came from that session itself and not a
    /// subagent of it (<see cref="WorkflowStepBridge"/>).
    /// </summary>
    public async Task<WorkflowStepDoneResult> StepDoneAsync(string userId, string sessionId, string? outcome, string? summary, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        var visit = await scope.Runs.GetStepBySessionAsync(sessionId).ConfigureAwait(false);
        if (visit is null)
            return new WorkflowStepDoneResult(false, WorkflowStepBridge.NotAStepMessage);

        WorkflowStepDoneResult? result = null;
        await WithRunAsync(scope, visit.RunId, async state =>
        {
            result = await FinishVisitAsync(scope, state, sessionId, outcome, summary).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false);

        // The agent hears back at once; starting the next step's session can take a while (a worktree, a harness).
        if (result is { Accepted: true })
            Track(Task.Run(() => ContinueAsync(userId, visit.RunId, ct), CancellationToken.None));

        return result ?? new WorkflowStepDoneResult(false, WorkflowStepBridge.NotAStepMessage);
    }

    private async Task<WorkflowStepDoneResult> FinishVisitAsync(Scope scope, RunState state, string sessionId, string? outcome, string? summary)
    {
        if (WorkflowRunStatus.IsFinished(state.Run.Status))
            return new WorkflowStepDoneResult(false, "This workflow run has ended, so there's no next step to start. Stop here.");

        var visit = state.Current;
        if (visit is null || visit.SessionId != sessionId)
            return new WorkflowStepDoneResult(false, "This step is already done. Stop here.");

        if (state.Workflow.Find(visit.StepId) is not WorkflowAgentStep step)
            return new WorkflowStepDoneResult(false, WorkflowStepBridge.NotAStepMessage);

        // Running, or waiting on the user after it stopped without the tool: the user may have replied to the agent.
        if (visit.Status is not (WorkflowRunStepStatus.Running or WorkflowRunStepStatus.Waiting) || visit.Outcome is not null)
            return new WorkflowStepDoneResult(false, "This step is already done. Stop here.");

        outcome = outcome?.Trim();
        if (string.IsNullOrEmpty(outcome) || !step.Outcomes.Contains(outcome))
        {
            return new WorkflowStepDoneResult(false,
                $"\"{outcome}\" isn't an outcome of this step. Call {Workflows.StepTool} again with outcome set to one of: {string.Join(", ", step.Outcomes)}.");
        }

        visit.Status = WorkflowRunStepStatus.Done;
        visit.Outcome = outcome;
        visit.Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        visit.FinishedAt = Now();
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);

        // Running again until the next step starts, so a restart in between carries on (RecoverAsync).
        state.Run.Status = WorkflowRunStatus.Running;
        state.Run.WaitingReason = null;
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        _watches.TryRemove(sessionId, out _);
        LogStepDone(state.Run.Id, step.Id, outcome);

        var target = step.Routes.GetValueOrDefault(outcome) ?? Next(state.Workflow, step.Id);
        return new WorkflowStepDoneResult(true, target == WorkflowTargets.End
            ? $"Recorded outcome {outcome}. That was the run's last step. Stop here."
            : $"Recorded outcome {outcome}. Stop here; Fleet takes it from here.");
    }

    /// <summary>Moves a run on from a step that just finished.</summary>
    private async Task ContinueAsync(string userId, string runId, CancellationToken ct)
    {
        try
        {
            using var scope = Begin(userId);
            await WithRunAsync(scope, runId, async state =>
            {
                if (state.Run.Status == WorkflowRunStatus.Running
                    && state.Current is { Status: WorkflowRunStepStatus.Done } visit
                    && state.Workflow.Find(visit.StepId) is WorkflowAgentStep step)
                {
                    await RouteAsync(scope, state, step, visit, checkLoop: true, ct).ConfigureAwait(false);
                }

                return state;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogContinueFailed(ex, runId);
        }
    }

    // ── The user ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The user answers a run that waits on them: a You step's choice (<c>choice:n</c>), an outcome for a step that
    /// stopped without one or went past its loop limit (<c>outcome:name</c>), or <c>retry</c> for a step Fleet couldn't
    /// start.
    /// </summary>
    public async Task<Result<WorkflowRunDto>> AnswerAsync(string userId, string runId, string choiceId, string? note, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        Result<WorkflowRunDto>? failure = null;
        var state = await WithRunAsync(scope, runId, async state =>
        {
            failure = await AnswerCoreAsync(scope, state, choiceId, note, ct).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false);

        if (state is null)
            return FleetError.NotFoundFor("WorkflowRun", runId);
        return failure ?? state;
    }

    private async Task<Result<WorkflowRunDto>?> AnswerCoreAsync(Scope scope, RunState state, string choiceId, string? note, CancellationToken ct)
    {
        var run = state.Run;
        var visit = state.Current;
        if (run.Status != WorkflowRunStatus.Waiting || visit is null)
            return new FleetError("WorkflowRun.NotWaiting", "This run isn't waiting on you.");

        var step = state.Workflow.Find(visit.StepId);
        switch (step)
        {
            case WorkflowYouStep you when choiceId.StartsWith("choice:", StringComparison.Ordinal)
                                          && int.TryParse(choiceId["choice:".Length..], out var index)
                                          && index >= 0 && index < you.Choices.Count:
            {
                var choice = you.Choices[index];
                note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                if (choice.Note && note is null)
                    return FleetError.ValidationError("Note", "Write what should change first.");

                visit.Status = WorkflowRunStepStatus.Decided;
                visit.Outcome = choice.Label;
                visit.Note = choice.Note ? note : null;
                visit.FinishedAt = Now();
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                LogDecided(run.Id, you.Id, choice.Label);
                await GoToAsync(scope, state, choice.To, choice.Note ? note : null, ct).ConfigureAwait(false);
                return null;
            }

            case WorkflowAgentStep agent when visit.SessionId is null && choiceId == "retry":
                await StartVisitAsync(scope, state, agent, visit, ct).ConfigureAwait(false);
                return null;

            case WorkflowAgentStep agent when choiceId.StartsWith("outcome:", StringComparison.Ordinal)
                                              && visit.SessionId is not null
                                              && agent.Outcomes.Contains(choiceId["outcome:".Length..]):
            {
                // The user picked it, so it goes where it leads even past a loop's maximum.
                visit.Status = WorkflowRunStepStatus.Done;
                visit.Outcome = choiceId["outcome:".Length..];
                visit.FinishedAt ??= Now();
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                _watches.TryRemove(visit.SessionId, out _);
                LogDecided(run.Id, agent.Id, visit.Outcome);
                await RouteAsync(scope, state, agent, visit, checkLoop: false, ct).ConfigureAwait(false);
                return null;
            }

            default:
                return FleetError.ValidationError("Choice", "That isn't one of the choices this run is waiting on.");
        }
    }

    /// <summary>
    /// Stops Fleet advancing the run. Its sessions stay as ordinary sessions: nothing is stopped or deleted.
    /// </summary>
    public async Task<Result<WorkflowRunDto>> EndAsync(string userId, string runId, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        var state = await WithRunAsync(scope, runId, async state =>
        {
            if (WorkflowRunStatus.IsFinished(state.Run.Status))
                return state;

            var at = state.Current is { } visit ? state.Workflow.Find(visit.StepId)?.Title : null;
            foreach (var session in state.Visits.Select(v => v.SessionId).OfType<string>())
                _watches.TryRemove(session, out _);
            await FinishRunAsync(scope, state, WorkflowRunStatus.Ended, at is null ? "Ended by you" : $"Ended by you at {at}").ConfigureAwait(false);
            LogEnded(state.Run.Id);
            return state;
        }).ConfigureAwait(false);

        return state is null ? FleetError.NotFoundFor("WorkflowRun", runId) : state;
    }

    // ── Moving between steps ───────────────────────────────────────────────────

    /// <summary>Goes where a finished agent step's outcome leads, unless it's one loop too many.</summary>
    private async Task RouteAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, bool checkLoop, CancellationToken ct)
    {
        var target = step.Routes.GetValueOrDefault(visit.Outcome!) ?? Next(state.Workflow, step.Id);
        if (checkLoop && IsBack(state.Workflow, step.Id, target) && step.MaxLoops is { } max)
        {
            // Every earlier visit of this step that sent work back, and this one.
            var sentBack = state.Visits.Count(v =>
                v.StepId == step.Id
                && v.Status == WorkflowRunStepStatus.Done
                && v.Outcome is { } o
                && IsBack(state.Workflow, step.Id, step.Routes.GetValueOrDefault(o) ?? Next(state.Workflow, step.Id)));
            if (sentBack > max)
            {
                var targetTitle = state.Workflow.Find(target)?.Title ?? target;
                await WaitAsync(scope, state, step.Id,
                    $"{step.Title} sent the work back to {targetTitle} {Times(sentBack)}; it may do that at most {Times(max)}. Pick where it goes next, or end the run.")
                    .ConfigureAwait(false);
                return;
            }
        }

        await GoToAsync(scope, state, target, note: null, ct).ConfigureAwait(false);
    }

    /// <summary>Starts <paramref name="target"/>, skipping optional steps that weren't switched on.</summary>
    private async Task GoToAsync(Scope scope, RunState state, string target, string? note, CancellationToken ct)
    {
        while (true)
        {
            if (target == WorkflowTargets.End)
            {
                await FinishRunAsync(scope, state, WorkflowRunStatus.Done, ResultOf(state)).ConfigureAwait(false);
                LogFinished(state.Run.Id);
                return;
            }

            switch (state.Workflow.Find(target))
            {
                case WorkflowAgentStep { Optional: true } skipped when !state.Options.OptionalSteps.Contains(skipped.Id):
                    await AddVisitAsync(scope, state, skipped.Id, WorkflowRunStepStatus.Skipped, note: null).ConfigureAwait(false);
                    target = Next(state.Workflow, skipped.Id);
                    continue;

                case WorkflowAgentStep agent:
                {
                    var visit = await AddVisitAsync(scope, state, agent.Id, WorkflowRunStepStatus.Running, note).ConfigureAwait(false);
                    await StartVisitAsync(scope, state, agent, visit, ct).ConfigureAwait(false);
                    return;
                }

                case WorkflowYouStep you:
                    await AddVisitAsync(scope, state, you.Id, WorkflowRunStepStatus.Waiting, note: null).ConfigureAwait(false);
                    await WaitAsync(scope, state, you.Id, you.Ask).ConfigureAwait(false);
                    return;

                default:
                    // A run's definition was checked when it started, so this is a bug; say so rather than hang.
                    await FinishRunAsync(scope, state, WorkflowRunStatus.Failed, $"The workflow has no step \"{target}\".").ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task StartVisitAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, CancellationToken ct)
    {
        var run = state.Run;
        run.Status = WorkflowRunStatus.Running;
        run.CurrentStepId = step.Id;
        run.WaitingReason = null;
        visit.Status = WorkflowRunStepStatus.Running;
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);

        var model = state.Options.StepModels.GetValueOrDefault(step.Id) ?? WorkflowModelChoice.Default;
        WorkflowStepSession started;
        try
        {
            started = await scope.Sessions.StartAsync(run, step, PromptFor(state, step, visit), model, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(ex, run.Id, step.Id);
            started = WorkflowStepSession.Failed(ex.Message);
        }

        if (started.SessionId is { } sessionId)
        {
            visit.SessionId = sessionId;
            await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
            if (run.WorktreePath is null && started.Directory is not null)
            {
                run.WorktreePath = started.Directory;
                run.Branch = started.Branch;
            }
        }

        if (started.Error is { } error)
        {
            visit.Status = WorkflowRunStepStatus.Waiting;
            await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);

            // Without a session there's nothing for the user to reply to, so the card offers to try again. With one
            // (the prompt didn't go through), the user can reply to the agent there instead.
            await WaitAsync(scope, state, step.Id, visit.SessionId is null
                ? $"Fleet couldn't start {step.Title}: {error}"
                : $"{step.Title} started, but its prompt didn't go through: {error} Reply to the agent in its session, or pick an outcome.")
                .ConfigureAwait(false);
            return;
        }

        _watches[started.SessionId!] = new StepWatch(run.Id, visit.Id, run.UserId);
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        LogStepStarted(run.Id, step.Id, visit.Visit, started.SessionId!);
    }

    /// <summary>The step's instructions with the run filled in, a note sent back, and the footer.</summary>
    internal static string PromptFor(RunState state, WorkflowAgentStep step, WorkflowRunStep visit)
    {
        var run = state.Run;
        var body = WorkflowYaml.Fill(step.Prompt, variable => variable switch
        {
            "request" => run.Request,
            "slug" => run.Slug,
            "run.branch" => run.Branch,
            "run.base" => run.BaseBranch ?? "the default branch",
            "previous.summary" => state.Visits.LastOrDefault(v => v.Id != visit.Id && v.Status == WorkflowRunStepStatus.Done && v.SessionId is not null)?.Summary,
            _ when WorkflowYaml.StepOfSummaryVariable(variable) is { } stepId
                => state.Visits.LastOrDefault(v => v.StepId == stepId && v.Status == WorkflowRunStepStatus.Done)?.Summary,
            _ => null,
        });

        var parts = new List<string> { body };
        if (visit.Note is { } note)
            parts.Add($"Sent back with a note:\n{note}");
        if (step.Skill is { } skill)
            parts.Add($"Use the {skill} skill.");
        parts.Add(Workflows.Footer(step.Outcomes));
        return string.Join("\n\n", parts);
    }

    private async Task<WorkflowRunStep> AddVisitAsync(Scope scope, RunState state, string stepId, string status, string? note)
    {
        var visit = new WorkflowRunStep
        {
            Id = Ulid.NewUlid().ToString(),
            RunId = state.Run.Id,
            StepId = stepId,
            Visit = state.Visits.Count(v => v.StepId == stepId && v.Status != WorkflowRunStepStatus.Skipped) + 1,
            Status = status,
            Note = note,
            StartedAt = Now(),
            FinishedAt = status == WorkflowRunStepStatus.Skipped ? Now() : null,
        };
        await scope.Runs.InsertStepAsync(visit).ConfigureAwait(false);
        state.Visits.Add(visit);
        state.Run.CurrentStepId = stepId;
        return visit;
    }

    private async Task WaitAsync(Scope scope, RunState state, string stepId, string reason)
    {
        state.Run.Status = WorkflowRunStatus.Waiting;
        state.Run.CurrentStepId = stepId;
        state.Run.WaitingReason = reason;
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        LogWaiting(state.Run.Id, stepId);
        state.NeedsYou = true;
    }

    private async Task FinishRunAsync(Scope scope, RunState state, string status, string? result)
    {
        state.Run.Status = status;
        state.Run.WaitingReason = null;
        state.Run.Result = result;
        state.Run.EndedAt = Now();
        await SaveRunAsync(scope, state).ConfigureAwait(false);
    }

    private async Task SaveRunAsync(Scope scope, RunState state)
    {
        state.Run.UpdatedAt = Now();
        await scope.Runs.UpdateAsync(state.Run).ConfigureAwait(false);
    }

    /// <summary>What a finished run says: the last agent step's summary when it's short (a PR's URL), else done.</summary>
    private static string ResultOf(RunState state)
    {
        var last = state.Visits.LastOrDefault(v => v.Status == WorkflowRunStepStatus.Done && v.SessionId is not null);
        var summary = last?.Summary?.Trim();
        if (summary is not null && PullRequestNumber(summary) is { } number)
            return $"PR #{number} opened";
        return state.Visits.LastOrDefault() is { Status: WorkflowRunStepStatus.Decided, Outcome: { } choice }
            ? $"Done · {choice}"
            : "Done";
    }

    private static string? PullRequestNumber(string text)
    {
        const string marker = "/pull/";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var digits = new string(text[(at + marker.Length)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static string Next(WorkflowDefinition workflow, string stepId)
    {
        var index = workflow.IndexOf(stepId);
        return index >= 0 && index + 1 < workflow.Steps.Count ? workflow.Steps[index + 1].Id : WorkflowTargets.End;
    }

    private static bool IsBack(WorkflowDefinition workflow, string from, string to)
        => to != WorkflowTargets.End && workflow.IndexOf(to) <= workflow.IndexOf(from);

    private static string Times(int count) => count switch
    {
        1 => "once",
        2 => "twice",
        _ => $"{count} times",
    };

    // ── Watching step sessions ─────────────────────────────────────────────────

    /// <summary>
    /// Called for every event the relay translates. A step whose session's turn ends without the tool waits on the
    /// user; Fleet doesn't prompt the agent again.
    /// </summary>
    public void Observe(string sessionId, DomainEvent? domainEvent)
    {
        if (domainEvent is null || !_watches.TryGetValue(sessionId, out var watch))
            return;

        switch (domainEvent)
        {
            case MessageCreated { Payload.Info.Role: "assistant" }:
            case MessageUpdated { Payload.Info.Role: "assistant" }:
                watch.Armed = true;
                break;
            case TurnFailed failed:
                watch.Armed = true;
                watch.Failure = failed.Payload.Error?.Message;
                break;
            case SessionIdled when watch.Armed:
                Track(Task.Run(() => StallIfStillRunningAsync(sessionId, watch)));
                break;
        }
    }

    private async Task StallIfStillRunningAsync(string sessionId, StepWatch watch)
    {
        try
        {
            // The tool's call reaches Fleet before the turn ends; the grace covers an idle that isn't the step's own
            // (a subagent's, before it binds) and a status that flickers.
            await Task.Delay(IdleGrace, time).ConfigureAwait(false);
            if (SessionActivityTracker.IsInTurn(activity.Get(sessionId)?.ActivityStatus))
                return;

            using var scope = Begin(watch.UserId);
            await WithRunAsync(scope, watch.RunId, async state =>
            {
                var visit = state.Current;
                if (visit is null || visit.Id != watch.VisitId || visit.Status != WorkflowRunStepStatus.Running
                    || WorkflowRunStatus.IsFinished(state.Run.Status))
                {
                    return state;
                }

                var title = state.Workflow.Find(visit.StepId)?.Title ?? visit.StepId;
                visit.Status = WorkflowRunStepStatus.Waiting;
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                _watches.TryRemove(sessionId, out _);
                await WaitAsync(scope, state, visit.StepId, watch.Failure is { } failure
                    ? $"{title} failed: {failure} Reply to the agent to carry on, or pick an outcome."
                    : $"{title} stopped without finishing the step. Reply to the agent to carry on, or pick an outcome.")
                    .ConfigureAwait(false);
                return state;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStallCheckFailed(ex, watch.RunId);
        }
    }

    private void Track(Task task)
    {
        lock (_gate)
            _inFlight.Add(task);
        _ = task.ContinueWith(
            done =>
            {
                lock (_gate)
                    _inFlight.Remove(done);
            },
            TaskScheduler.Default);
    }

    // ── After a restart ────────────────────────────────────────────────────────

    /// <summary>
    /// Picks up every unfinished run where it was. A step that finished but whose next step hadn't started goes on. A
    /// step that was running lost its turn with the restart, so it waits on the user; Fleet doesn't prompt it again.
    /// Runs keep waiting where they waited.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        IReadOnlyList<WorkflowRun> runs;
        using (var all = scopeFactory.CreateScope())
            runs = await all.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().ListUnfinishedAsync().ConfigureAwait(false);

        foreach (var unfinished in runs)
        {
            try
            {
                using var scope = Begin(unfinished.UserId);
                if (!await scope.Feature.IsEnabledAsync().ConfigureAwait(false))
                    continue;

                await WithRunAsync(scope, unfinished.Id, async state =>
                {
                    await RecoverRunAsync(scope, state, ct).ConfigureAwait(false);
                    return state;
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoverFailed(ex, unfinished.Id);
            }
        }
    }

    private async Task RecoverRunAsync(Scope scope, RunState state, CancellationToken ct)
    {
        var visit = state.Current;
        if (state.Run.Status == WorkflowRunStatus.Waiting)
        {
            // A step that stopped without the tool can still finish if the user replies to it.
            return;
        }

        if (visit is null)
        {
            await GoToAsync(scope, state, state.Workflow.Steps[0].Id, note: null, ct).ConfigureAwait(false);
            return;
        }

        var step = state.Workflow.Find(visit.StepId);
        switch (visit.Status)
        {
            case WorkflowRunStepStatus.Running when step is WorkflowAgentStep agent && visit.SessionId is null:
                // It stopped before its session existed.
                await StartVisitAsync(scope, state, agent, visit, ct).ConfigureAwait(false);
                break;

            case WorkflowRunStepStatus.Running when step is not null:
                visit.Status = WorkflowRunStepStatus.Waiting;
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                await WaitAsync(scope, state, visit.StepId,
                    $"Fleet restarted while {step.Title} was running, which stopped its turn. Reply to the agent to carry on, or pick an outcome.")
                    .ConfigureAwait(false);
                break;

            case WorkflowRunStepStatus.Done when step is WorkflowAgentStep agent:
                await RouteAsync(scope, state, agent, visit, checkLoop: true, ct).ConfigureAwait(false);
                break;

            case WorkflowRunStepStatus.Skipped:
                await GoToAsync(scope, state, Next(state.Workflow, visit.StepId), note: null, ct).ConfigureAwait(false);
                break;

            case WorkflowRunStepStatus.Decided when step is WorkflowYouStep you
                                                   && you.Choices.FirstOrDefault(c => c.Label == visit.Outcome) is { } choice:
                await GoToAsync(scope, state, choice.To, choice.Note ? visit.Note : null, ct).ConfigureAwait(false);
                break;
        }
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a run as its owner, runs <paramref name="change"/> under the run's lock, then tells the client. Null when
    /// the run isn't the user's.
    /// </summary>
    private async Task<WorkflowRunDto?> WithRunAsync(Scope scope, string runId, Func<RunState, Task<RunState>> change)
    {
        var gate = _locks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        RunState state;
        try
        {
            var run = await scope.Runs.GetAsync(runId).ConfigureAwait(false);
            if (run is null)
                return null;

            var workflow = WorkflowYaml.Parse(run.Definition, run.WorkflowId).Definition
                ?? throw new InvalidOperationException($"Workflow run {runId} has a definition that doesn't read.");
            var visits = (await scope.Runs.ListStepsAsync(runId).ConfigureAwait(false)).ToList();
            state = await change(new RunState(run, workflow, WorkflowRunOptions.Read(run.Options), visits)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var dto = WorkflowRunView.Build(state.Run, state.Workflow, state.Visits);
        try
        {
            await scope.Events.ChangedAsync(dto, state.Run.UserId).ConfigureAwait(false);
            if (state.NeedsYou && dto.Status == WorkflowRunStatus.Waiting)
                await scope.Events.NeedsYouAsync(dto, state.Run.UserId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBroadcastFailed(ex, runId);
        }

        return dto;
    }

    private Scope Begin(string userId)
    {
        var scope = scopeFactory.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
        return new Scope(scope, user);
    }

    private string Now() => time.GetUtcNow().UtcDateTime.ToString("O");

    /// <summary>A run loaded under its lock: its workflow as it was when it started, its options and its visits.</summary>
    internal sealed class RunState(WorkflowRun run, WorkflowDefinition workflow, WorkflowRunOptions options, List<WorkflowRunStep> visits)
    {
        public WorkflowRun Run { get; } = run;
        public WorkflowDefinition Workflow { get; } = workflow;
        public WorkflowRunOptions Options { get; } = options;
        public List<WorkflowRunStep> Visits { get; } = visits;

        /// <summary>The visit the run is on: always the last one.</summary>
        public WorkflowRunStep? Current => Visits.LastOrDefault();

        /// <summary>The change made the run wait on the user, so they're told.</summary>
        public bool NeedsYou { get; set; }
    }

    /// <summary>A running step's session, until its turn ends.</summary>
    private sealed class StepWatch(string runId, string visitId, string userId)
    {
        public string RunId { get; } = runId;
        public string VisitId { get; } = visitId;
        public string UserId { get; } = userId;

        /// <summary>The step's turn has started: an assistant message, or a failure.</summary>
        public volatile bool Armed;
        public string? Failure { get; set; }
    }

    private sealed class Scope(IServiceScope scope, IDisposable user) : IDisposable
    {
        public IWorkflowRunRepository Runs { get; } = scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>();
        public IWorkflowStepSessions Sessions => scope.ServiceProvider.GetRequiredService<IWorkflowStepSessions>();
        public IWorkflowRunEvents Events => scope.ServiceProvider.GetRequiredService<IWorkflowRunEvents>();
        public WorkflowsFeature Feature => scope.ServiceProvider.GetRequiredService<WorkflowsFeature>();

        public void Dispose()
        {
            user.Dispose();
            scope.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} started ({WorkflowId})")]
    private partial void LogStarted(string runId, string workflowId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: {StepId} visit {Visit} started in session {SessionId}")]
    private partial void LogStepStarted(string runId, string stepId, int visit, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: {StepId} finished with {Outcome}")]
    private partial void LogStepDone(string runId, string stepId, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: {StepId} decided {Choice}")]
    private partial void LogDecided(string runId, string stepId, string choice);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} waits on the user at {StepId}")]
    private partial void LogWaiting(string runId, string stepId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} finished")]
    private partial void LogFinished(string runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} ended by the user")]
    private partial void LogEnded(string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't start {StepId}")]
    private partial void LogStartFailed(Exception ex, string runId, string stepId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't move on from the step that finished")]
    private partial void LogContinueFailed(Exception ex, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't check whether its step stopped")]
    private partial void LogStallCheckFailed(Exception ex, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't pick it up after the restart")]
    private partial void LogRecoverFailed(Exception ex, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't tell the client it changed")]
    private partial void LogBroadcastFailed(Exception ex, string runId);
}
