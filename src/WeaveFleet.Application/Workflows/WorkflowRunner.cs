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

/// <summary>Starts the session for an agent step, and talks to it when the user moves on from a step they finish.</summary>
public interface IWorkflowStepSessions
{
    /// <param name="userFinishes">The user finishes the step, so its session doesn't get the step tool.</param>
    Task<WorkflowStepSession> StartAsync(WorkflowRun run, WorkflowAgentStep agentStep, string prompt, WorkflowModelChoice model, bool userFinishes, CancellationToken ct);

    /// <summary>Sends Fleet's wrap-up prompt into a step's session.</summary>
    Task<WorkflowPromptSent> PromptAsync(string sessionId, string text, CancellationToken ct);

    /// <summary>The text of the last reply to <paramref name="messageId"/>, or null when there's none.</summary>
    Task<string?> ReplyToAsync(string sessionId, string messageId, CancellationToken ct);
}

/// <summary>The session a step started in, or why it couldn't start.</summary>
/// <param name="Directory">Where the session works: the run's worktree.</param>
/// <param name="PromptMessageId">The id the harness was given for the step's prompt.</param>
public sealed record WorkflowStepSession(string? SessionId, string? Directory, string? Branch, string? Error, string? PromptMessageId = null)
{
    public static WorkflowStepSession Failed(string error, string? sessionId = null) => new(sessionId, null, null, error);
}

/// <summary>A prompt Fleet sent into a step's session: the id the harness was given for it, or why it didn't go.</summary>
public sealed record WorkflowPromptSent(string? MessageId, string? Error);

/// <summary>Tells the client a run changed, and the user when it needs them.</summary>
public interface IWorkflowRunEvents
{
    Task ChangedAsync(WorkflowRunDto run, string userId);
    Task NeedsYouAsync(WorkflowRunDto run, string userId);
}

/// <summary>How a <c>fleet_step_done</c> call went, in words the agent reads.</summary>
public sealed record WorkflowStepDoneResult(bool Accepted, string Message);

/// <summary>
/// Moves workflow runs from step to step. A step ends when its session calls <see cref="FleetWorkflows.StepTool"/>, or,
/// for a step the user finishes, when the user moves on and the wrap-up turn Fleet sends for it ends; the outcome picks
/// the next step, once the step's declared files are there. Fleet never advances on idle alone: a step whose turn ends
/// without the tool waits on the user, and so does a You step, a loop past its maximum and a missing file. The wrap-up
/// is the only prompt Fleet sends an agent, and only when the user presses Move on.
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

    /// <summary>What the agent hears if it calls the step tool in a step the user finishes (it shouldn't have it).</summary>
    public const string UserFinishesMessage =
        "You and the user are working through this step together, and only the user can end it. Carry on with what they asked.";

    /// <summary>The choice that goes on past a missing file or a wrap-up that didn't finish.</summary>
    public const string MoveOnAnywayChoice = "move-on-anyway";

    /// <summary>Tries again to start a step Fleet couldn't start, or whose skill was off.</summary>
    public const string RetryChoice = "retry";

    /// <summary>Starts a step whose skill is off without it, for the rest of the run.</summary>
    public const string WithoutSkillChoice = "without-skill";

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

        if (visit.Finish == WorkflowFinishers.You)
            return new WorkflowStepDoneResult(false, UserFinishesMessage);

        // Running, or waiting on the user after it stopped without the tool: the user may have replied to the agent.
        if (visit.Status is not (WorkflowRunStepStatus.Running or WorkflowRunStepStatus.Waiting) || visit.Outcome is not null)
            return new WorkflowStepDoneResult(false, "This step is already done. Stop here.");

        outcome = outcome?.Trim();
        if (string.IsNullOrEmpty(outcome) || !step.Outcomes.Contains(outcome))
        {
            return new WorkflowStepDoneResult(false,
                $"\"{outcome}\" isn't an outcome of this step. Call {FleetWorkflows.StepTool} again with outcome set to one of: {string.Join(", ", step.Outcomes)}.");
        }

        visit.Status = WorkflowRunStepStatus.Done;
        visit.Outcome = outcome;
        visit.Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        visit.FinishedAt = Now();
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);

        // Running again until the next step starts, so a restart in between carries on (RecoverAsync).
        state.Run.Status = WorkflowRunStatus.Running;
        state.Run.WaitingReason = null;
        state.Run.WaitingKind = null;
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
                    await RouteAsync(scope, state, step, visit, checkLoop: true, checkFiles: true, ct).ConfigureAwait(false);
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
    /// stopped without one or went past its loop limit (<c>outcome:name</c>), <c>retry</c> for a step Fleet couldn't
    /// start, or <see cref="MoveOnAnywayChoice"/> past a missing file or a wrap-up that didn't finish.
    /// </summary>
    /// <param name="checkWithMe">
    /// With a You step's choice that goes on to a step: how involved the user wants to be from here ("Approve, check
    /// with me after each step"). Null leaves Check with me as it is.
    /// </param>
    public async Task<Result<WorkflowRunDto>> AnswerAsync(string userId, string runId, string choiceId, string? note, bool? checkWithMe = null, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        Result<WorkflowRunDto>? failure = null;
        var state = await WithRunAsync(scope, runId, async state =>
        {
            failure = await AnswerCoreAsync(scope, state, choiceId, note, checkWithMe, ct).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false);

        if (state is null)
            return FleetError.NotFoundFor("WorkflowRun", runId);
        return failure ?? state;
    }

    private async Task<Result<WorkflowRunDto>?> AnswerCoreAsync(Scope scope, RunState state, string choiceId, string? note, bool? checkWithMe, CancellationToken ct)
    {
        var run = state.Run;
        var visit = state.Current;
        if (run.Status != WorkflowRunStatus.Waiting || visit is null)
            return new FleetError("WorkflowRun.NotWaiting", "This run isn't waiting on you.");

        var step = state.Workflow.Find(visit.StepId);
        if (run.WaitingKind is WorkflowWaitingKinds.MissingFiles or WorkflowWaitingKinds.WrapUpFailed && step is WorkflowAgentStep waited)
        {
            if (choiceId != MoveOnAnywayChoice || visit.Outcome is null)
                return FleetError.ValidationError("Choice", "That isn't one of the choices this run is waiting on.");

            // Past the files check, and for a wrap-up that didn't finish, without a summary.
            visit.Status = WorkflowRunStepStatus.Done;
            visit.FinishedAt ??= Now();
            await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
            if (visit.SessionId is { } waitedSession)
                _watches.TryRemove(waitedSession, out _);
            LogMovedOnAnyway(run.Id, waited.Id);
            await RouteAsync(scope, state, waited, visit, checkLoop: false, checkFiles: false, ct).ConfigureAwait(false);
            return null;
        }

        if (run.WaitingKind == WorkflowWaitingKinds.SkillOff && step is WorkflowAgentStep skilled)
        {
            switch (choiceId)
            {
                case RetryChoice when await scope.Skills.IsOffAsync(skilled.Skill).ConfigureAwait(false):
                    return FleetError.ValidationError("Choice", $"{skilled.Skill} is still off. Turn it on in Settings → Skills, then retry.");
                case RetryChoice:
                    break;
                case WithoutSkillChoice:
                    if (!state.Options.WithoutSkills.Contains(skilled.Id))
                        state.Options.WithoutSkills.Add(skilled.Id);
                    run.Options = state.Options.Write();
                    LogWithoutSkill(run.Id, skilled.Id, skilled.Skill ?? string.Empty);
                    break;
                default:
                    return FleetError.ValidationError("Choice", "That isn't one of the choices this run is waiting on.");
            }

            await StartVisitAsync(scope, state, skilled, visit, ct).ConfigureAwait(false);
            return null;
        }

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

                // Saved before the next step starts, which is the first one it applies to.
                if (checkWithMe is { } on && !choice.Note && choice.To != WorkflowTargets.End)
                {
                    state.Options.CheckWithMe = on;
                    run.Options = state.Options.Write();
                    await SaveRunAsync(scope, state).ConfigureAwait(false);
                }

                await GoToAsync(scope, state, choice.To, choice.Note ? note : null, ct).ConfigureAwait(false);
                return null;
            }

            case WorkflowAgentStep agent when visit.SessionId is null && choiceId == RetryChoice:
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
                await RouteAsync(scope, state, agent, visit, checkLoop: false, checkFiles: true, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Switches "Check with me after each step" on or off, from the run's header. It applies from the next step: the
    /// step that's running keeps the way it started, so its agent is never given or denied the step tool mid-turn.
    /// </summary>
    public async Task<Result<WorkflowRunDto>> SetCheckWithMeAsync(string userId, string runId, bool on, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        Result<WorkflowRunDto>? failure = null;
        var state = await WithRunAsync(scope, runId, async state =>
        {
            if (WorkflowRunStatus.IsFinished(state.Run.Status))
            {
                failure = new FleetError("WorkflowRun.Finished", "This run has finished, so there are no steps left to check with you.");
                return state;
            }

            state.Options.CheckWithMe = on;
            state.Run.Options = state.Options.Write();
            await SaveRunAsync(scope, state).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false);

        if (state is null)
            return FleetError.NotFoundFor("WorkflowRun", runId);
        return failure ?? state;
    }

    // ── Steps you finish ───────────────────────────────────────────────────────

    /// <summary>
    /// The user moves on from a step they finish. Fleet sends one prompt into the step's session: bring the declared
    /// files up to date with what was agreed, then reply with a short summary for the next step. The run moves on
    /// when the turn that answers that prompt ends (<see cref="Observe"/>).
    /// </summary>
    /// <param name="outcome">The outcome the user picked; a step with one outcome needs none.</param>
    /// <param name="note">The user's note for the next step.</param>
    public async Task<Result<WorkflowRunDto>> MoveOnAsync(string userId, string runId, string? outcome, string? note, CancellationToken ct = default)
    {
        using var scope = Begin(userId);
        Result<WorkflowRunDto>? failure = null;
        var state = await WithRunAsync(scope, runId, async state =>
        {
            failure = await MoveOnCoreAsync(scope, state, outcome, note, ct).ConfigureAwait(false);
            return state;
        }).ConfigureAwait(false);

        if (state is null)
            return FleetError.NotFoundFor("WorkflowRun", runId);
        return failure ?? state;
    }

    private async Task<Result<WorkflowRunDto>?> MoveOnCoreAsync(Scope scope, RunState state, string? outcome, string? note, CancellationToken ct)
    {
        var visit = state.Current;
        if (state.Run.Status != WorkflowRunStatus.Running
            || visit is not { Status: WorkflowRunStepStatus.Running, Finish: WorkflowFinishers.You, SessionId: { } sessionId }
            || state.Workflow.Find(visit.StepId) is not WorkflowAgentStep step)
        {
            return new FleetError("WorkflowRun.NotWithYou", "There's no step open for you to move on from.");
        }

        outcome = string.IsNullOrWhiteSpace(outcome) ? (step.Outcomes.Count == 1 ? step.Outcomes[0] : null) : outcome.Trim();
        if (outcome is null || !step.Outcomes.Contains(outcome))
            return FleetError.ValidationError("Outcome", $"Pick how {step.Title} went: {string.Join(", ", step.Outcomes)}.");

        if (LoopsLeft(state, step, outcome) is { Left: <= 0 } loops)
        {
            var target = state.Workflow.Find(Target(state.Workflow, step, outcome))?.Title;
            return FleetError.ValidationError("Outcome",
                $"{step.Title} has sent the work back to {target} {Times(loops.Used)}, the most this run allows. Pick another outcome, or end the run.");
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var files = FilesOf(state, step);
        var text = WrapUpPrompt(NextTitle(state, step, outcome), step.Outcomes.Count > 1 ? outcome : null, files, note);

        // Watched before it's sent: a quick model can answer before the send returns.
        var watch = new StepWatch(state.Run.Id, visit.Id, state.Run.UserId, WatchKind.WrapUp);
        _watches[sessionId] = watch;

        var sent = await scope.Sessions.PromptAsync(sessionId, text, ct).ConfigureAwait(false);
        if (sent.MessageId is null)
        {
            _watches.TryRemove(sessionId, out _);
            return new FleetError("WorkflowRun.WrapUpFailed", $"Fleet couldn't ask the agent to wrap up: {sent.Error ?? "the prompt didn't go through."}");
        }

        visit.Status = WorkflowRunStepStatus.WrappingUp;
        visit.Outcome = outcome;
        visit.HandOffNote = note;
        visit.WrapUpMessageId = sent.MessageId;
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        LogMovingOn(state.Run.Id, step.Id, outcome);

        if (watch.Sent(sent.MessageId))
            Track(Task.Run(() => SettleAsync(visit.SessionId!, watch), CancellationToken.None));
        return null;
    }

    /// <summary>
    /// The one prompt Fleet sends into a step the user finishes, when they move on: "The user is moving on to Plan.
    /// Update docs/design/x.md with everything agreed in this conversation, then reply with a short summary for Plan."
    /// </summary>
    /// <param name="next">The next step's title, or null when the run ends here.</param>
    /// <param name="outcome">The outcome the user picked, for a step with more than one.</param>
    internal static string WrapUpPrompt(string? next, string? outcome, IReadOnlyList<string> files, string? note)
    {
        var moving = next is null
            ? outcome is null ? "The user is finishing the run here." : $"The user chose {outcome} and is finishing the run here."
            : outcome is null ? $"The user is moving on to {next}." : $"The user chose {outcome} and is moving on to {next}.";
        var summary = next is null ? "a short summary of where things stand" : $"a short summary for {next}";
        var ask = files.Count > 0
            ? $"Update {WorkflowYaml.ListFiles(files)} with everything agreed in this conversation, then reply with {summary}."
            : $"Reply with {summary}.";
        var text = $"{moving} {ask}";
        return note is null ? text : $"{text}\n\nTheir note: {note}";
    }

    /// <summary>How many times a step may still send work back on an outcome; null when the outcome doesn't loop.</summary>
    internal static (int Used, int Max, int Left)? LoopsLeft(RunState state, WorkflowAgentStep step, string outcome)
    {
        if (!IsBack(state.Workflow, step.Id, Target(state.Workflow, step, outcome)) || step.MaxLoops is not { } max)
            return null;

        var used = state.Visits.Count(v =>
            v.StepId == step.Id
            && v.Status == WorkflowRunStepStatus.Done
            && v.Outcome is { } o
            && IsBack(state.Workflow, step.Id, Target(state.Workflow, step, o)));
        return (used, max, max - used);
    }

    /// <summary>The title of the step an outcome leads to, past optional steps that are off; null for the end.</summary>
    internal static string? NextTitle(RunState state, WorkflowAgentStep step, string outcome)
    {
        var target = Target(state.Workflow, step, outcome);
        while (target != WorkflowTargets.End && state.Workflow.Find(target) is { } next)
        {
            if (next is WorkflowAgentStep { Optional: true } skipped && !state.Options.OptionalSteps.Contains(skipped.Id))
            {
                target = Next(state.Workflow, skipped.Id);
                continue;
            }

            return next.Title;
        }

        return null;
    }

    /// <summary>A step's declared files with this run filled in.</summary>
    internal static IReadOnlyList<string> FilesOf(RunState state, WorkflowAgentStep step)
        => WorkflowYaml.FilesOf(step, variable => variable switch
        {
            "slug" => state.Run.Slug,
            "run.branch" => state.Run.Branch,
            _ => null,
        });

    // ── Moving between steps ───────────────────────────────────────────────────

    /// <summary>
    /// Goes where a finished agent step's outcome leads, unless it's one loop too many or a file the step declares
    /// isn't there.
    /// </summary>
    private async Task RouteAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, bool checkLoop, bool checkFiles, CancellationToken ct)
    {
        var target = Target(state.Workflow, step, visit.Outcome!);
        if (checkLoop && IsBack(state.Workflow, step.Id, target) && step.MaxLoops is { } max)
        {
            // Every earlier visit of this step that sent work back, and this one.
            var sentBack = LoopsLeft(state, step, visit.Outcome!)?.Used ?? 0;
            if (sentBack > max)
            {
                var targetTitle = state.Workflow.Find(target)?.Title ?? target;
                await WaitAsync(scope, state, step.Id,
                    $"{step.Title} sent the work back to {targetTitle} {Times(sentBack)}; it may do that at most {Times(max)}. Pick where it goes next, or end the run.")
                    .ConfigureAwait(false);
                return;
            }
        }

        // Before the next step starts, so it never starts from files that aren't there.
        if (checkFiles && target != WorkflowTargets.End && step.Writes.Count > 0)
        {
            var missing = scope.Files.Missing(state.Run.WorktreePath, FilesOf(state, step));
            if (missing.Count > 0)
            {
                await WaitAsync(scope, state, step.Id, MissingFilesMessage(step, missing), WorkflowWaitingKinds.MissingFiles).ConfigureAwait(false);
                if (visit.SessionId is { } session)
                    _watches[session] = StepWatch.ForReply(state.Run.Id, visit, state.Run.UserId);
                return;
            }

            visit.FilesChecked = true;
            await CommitFilesAsync(scope, state, step, visit).ConfigureAwait(false);
            await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
        }

        await GoToAsync(scope, state, target, note: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Once a step's declared files pass the check, Fleet commits the ones that are new or changed, so they're in the
    /// pull request; a path git ignores stays out. A failed commit is recorded on the visit and the run goes on.
    /// </summary>
    private async Task CommitFilesAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit)
    {
        var commit = await scope.Files.CommitAsync(state.Run.WorktreePath, FilesOf(state, step), step.Title, CancellationToken.None).ConfigureAwait(false);
        visit.FilesCommit = commit.Commit;
        visit.FilesCommitError = commit.Error;
        if (commit.Error is { } error)
            LogCommitFailed(state.Run.Id, step.Id, error);
        else if (commit.Commit is { } sha)
            LogCommitted(state.Run.Id, step.Id, sha);
    }

    private static string MissingFilesMessage(WorkflowAgentStep step, IReadOnlyList<string> missing)
        => $"{step.Title} declares {WorkflowYaml.ListFiles(missing)}, but {(missing.Count == 1 ? "it isn't" : "they aren't")} in the run's worktree. Reply to the agent in its session, or move on anyway.";

    /// <summary>The step an outcome leads to: its route, or the next step in the file.</summary>
    private static string Target(WorkflowDefinition workflow, WorkflowAgentStep step, string outcome)
        => step.Routes.GetValueOrDefault(outcome) ?? Next(workflow, step.Id);

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

        // The skill was on when the run started, but a run can wait for days; a step never starts without it unasked.
        if (!state.Options.WithoutSkills.Contains(step.Id) && await scope.Skills.IsOffAsync(step.Skill).ConfigureAwait(false))
        {
            visit.Status = WorkflowRunStepStatus.Waiting;
            await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
            await WaitAsync(scope, state, step.Id, WorkflowSkills.NowOffMessage(step), WorkflowWaitingKinds.SkillOff).ConfigureAwait(false);
            return;
        }

        run.Status = WorkflowRunStatus.Running;
        run.CurrentStepId = step.Id;
        run.WaitingReason = null;
        run.WaitingKind = null;
        visit.Status = WorkflowRunStepStatus.Running;

        // Decided as the step starts and kept for good: switching Check with me later changes the steps after it. A step
        // whose file says finish: agent or finish: you is that whatever Check with me says.
        var userFinishes = step.UserFinishes(state.Options.CheckWithMe);
        visit.Finish = userFinishes ? WorkflowFinishers.You : WorkflowFinishers.Agent;
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);

        var model = state.Options.StepModels.GetValueOrDefault(step.Id) ?? WorkflowModelChoice.Default;
        WorkflowStepSession started;
        try
        {
            started = await scope.Sessions.StartAsync(run, step, PromptFor(state, step, visit), model, userFinishes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(ex, run.Id, step.Id);
            started = WorkflowStepSession.Failed(ex.Message);
        }

        if (started.SessionId is { } sessionId)
        {
            visit.SessionId = sessionId;
            visit.PromptMessageId = started.PromptMessageId;
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

        // A step the user finishes is a conversation: a turn ending there is just a turn.
        if (!userFinishes)
            _watches[started.SessionId!] = new StepWatch(run.Id, visit.Id, run.UserId, WatchKind.Stall);
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        LogStepStarted(run.Id, step.Id, visit.Visit, started.SessionId!);
    }

    /// <summary>
    /// The step's instructions with the run filled in, a note sent back, the user's note from the step before, and the
    /// footer. A step the user finishes has no footer: it has no step tool to call.
    /// </summary>
    internal static string PromptFor(RunState state, WorkflowAgentStep step, WorkflowRunStep visit)
    {
        var run = state.Run;
        var previous = state.Visits.LastOrDefault(v => v.Id != visit.Id && v.Status == WorkflowRunStepStatus.Done && v.SessionId is not null);
        var body = WorkflowYaml.Fill(step.Prompt, variable => variable switch
        {
            "request" => run.Request,
            "slug" => run.Slug,
            "run.branch" => run.Branch,
            "run.base" => run.BaseBranch ?? "the default branch",
            "previous.summary" => previous?.Summary,
            "previous.files" => previous is not null && state.Workflow.Find(previous.StepId) is WorkflowAgentStep before
                ? WorkflowYaml.ListFiles(FilesOf(state, before))
                : null,
            _ when WorkflowYaml.StepOfSummaryVariable(variable) is { } stepId
                => state.Visits.LastOrDefault(v => v.StepId == stepId && v.Status == WorkflowRunStepStatus.Done)?.Summary,
            _ when WorkflowYaml.StepOfFilesVariable(variable) is { } stepId
                => state.Visits.Any(v => v.StepId == stepId && v.Status == WorkflowRunStepStatus.Done)
                   && state.Workflow.Find(stepId) is WorkflowAgentStep that
                    ? WorkflowYaml.ListFiles(FilesOf(state, that))
                    : null,
            _ => null,
        });

        var parts = new List<string> { body };
        if (visit.Note is { } note)
            parts.Add($"Sent back with a note:\n{note}");

        // The note the user wrote for the next step when they moved on; not for the same step when it comes round again.
        if (previous is { HandOffNote: { } handOff } && previous.StepId != step.Id)
            parts.Add($"Note from the user:\n{handOff}");
        if (step.Skill is { } skill && !state.Options.WithoutSkills.Contains(step.Id))
            parts.Add($"Use the {skill} skill.");
        if (visit.Finish != WorkflowFinishers.You)
            parts.Add(FleetWorkflows.Footer(step.Outcomes));
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

    /// <param name="kind">
    /// <see cref="WorkflowWaitingKinds.MissingFiles"/> or <see cref="WorkflowWaitingKinds.WrapUpFailed"/>; null when the
    /// visit says what the wait is.
    /// </param>
    private async Task WaitAsync(Scope scope, RunState state, string stepId, string reason, string? kind = null)
    {
        state.Run.Status = WorkflowRunStatus.Waiting;
        state.Run.CurrentStepId = stepId;
        state.Run.WaitingReason = reason;
        state.Run.WaitingKind = kind;
        await SaveRunAsync(scope, state).ConfigureAwait(false);
        LogWaiting(state.Run.Id, stepId);
        state.NeedsYou = true;
    }

    private async Task FinishRunAsync(Scope scope, RunState state, string status, string? result)
    {
        state.Run.Status = status;
        state.Run.WaitingReason = null;
        state.Run.WaitingKind = null;
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
    /// Called for every event the relay translates. It watches a running step's turn, to see whether it ends without
    /// the tool; the wrap-up turn of a step the user moved on from; and, while a step waits on a missing file or a
    /// wrap-up that didn't finish, the next turn the user starts in its session. Fleet doesn't prompt the agent again.
    /// </summary>
    public void Observe(string sessionId, DomainEvent? domainEvent)
    {
        if (domainEvent is null || !_watches.TryGetValue(sessionId, out var watch))
            return;

        switch (domainEvent)
        {
            case MessageCreated { Payload.Info: { Role: "assistant" } info }:
                watch.Saw(info.Id, info.ParentId);
                break;
            case MessageUpdated { Payload.Info: { Role: "assistant" } info }:
                watch.Saw(info.Id, info.ParentId);
                break;
            case TurnFailed failed:
                watch.Failed(failed.Payload.MessageId, failed.Payload.Error?.Message);
                break;
            case SessionIdled when watch.Idled():
                Track(Task.Run(() => SettleAsync(sessionId, watch)));
                break;
        }
    }

    /// <summary>A watched turn went idle: once it's really over, see where that leaves its step.</summary>
    private async Task SettleAsync(string sessionId, StepWatch watch)
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
                // Only the watch the session has now counts; a newer one replaced it.
                if (!_watches.TryGetValue(sessionId, out var current) || current != watch)
                    return state;

                var visit = state.Current;
                if (visit is null || visit.Id != watch.VisitId || WorkflowRunStatus.IsFinished(state.Run.Status)
                    || state.Workflow.Find(visit.StepId) is not WorkflowAgentStep step)
                {
                    _watches.TryRemove(sessionId, out _);
                    return state;
                }

                switch (watch.Kind)
                {
                    case WatchKind.Stall:
                        await StalledAsync(scope, state, step, visit, sessionId, watch).ConfigureAwait(false);
                        break;
                    case WatchKind.WrapUp:
                        await WrappedUpAsync(scope, state, step, visit, sessionId, watch).ConfigureAwait(false);
                        break;
                    case WatchKind.Reply:
                        await RepliedAsync(scope, state, step, visit, sessionId).ConfigureAwait(false);
                        break;
                }

                return state;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStallCheckFailed(ex, watch.RunId);
        }
    }

    /// <summary>A step's turn ended without the tool: it waits on the user.</summary>
    private async Task StalledAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, string sessionId, StepWatch watch)
    {
        if (visit.Status != WorkflowRunStepStatus.Running)
            return;

        visit.Status = WorkflowRunStepStatus.Waiting;
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
        _watches.TryRemove(sessionId, out _);
        await WaitAsync(scope, state, visit.StepId, watch.Failure is { } failure
            ? $"{step.Title} failed: {failure} Reply to the agent to carry on, or pick an outcome."
            : $"{step.Title} stopped without finishing the step. Reply to the agent to carry on, or pick an outcome.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The turn that answered the wrap-up prompt ended. Its reply is the step's summary, and the run goes where the
    /// user's outcome leads once the declared files are there. A wrap-up that failed waits on the user.
    /// </summary>
    private async Task WrappedUpAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, string sessionId, StepWatch watch)
    {
        if (visit.Status != WorkflowRunStepStatus.WrappingUp || visit.WrapUpMessageId != watch.MessageId)
            return;

        _watches.TryRemove(sessionId, out _);
        if (watch.Failure is { } failure)
        {
            await WaitAsync(scope, state, step.Id,
                $"{step.Title}'s wrap-up failed: {failure} Reply to the agent in its session, or move on anyway.",
                WorkflowWaitingKinds.WrapUpFailed).ConfigureAwait(false);
            _watches[sessionId] = StepWatch.ForReply(state.Run.Id, visit, state.Run.UserId);
            return;
        }

        string? reply = null;
        try
        {
            reply = await scope.Sessions.ReplyToAsync(sessionId, visit.WrapUpMessageId!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReplyReadFailed(ex, state.Run.Id, step.Id);
        }

        visit.Summary = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
        visit.Status = WorkflowRunStepStatus.Done;
        visit.FinishedAt = Now();
        await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
        LogStepDone(state.Run.Id, step.Id, visit.Outcome!);

        // The user picked the outcome, and Move on checked it against the loop's maximum.
        await RouteAsync(scope, state, step, visit, checkLoop: false, checkFiles: true, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The user replied in a step that waits on a missing file or a wrap-up that didn't finish, and that turn ended.
    /// With the files there now, the run goes on; after a failed wrap-up, the step is the user's again to move on from.
    /// </summary>
    private async Task RepliedAsync(Scope scope, RunState state, WorkflowAgentStep step, WorkflowRunStep visit, string sessionId)
    {
        if (state.Run.Status != WorkflowRunStatus.Waiting)
            return;

        switch (state.Run.WaitingKind)
        {
            case WorkflowWaitingKinds.MissingFiles:
            {
                var missing = scope.Files.Missing(state.Run.WorktreePath, FilesOf(state, step));
                if (missing.Count > 0)
                {
                    // Still waiting, and the user already knows: just say what's missing now.
                    state.Run.WaitingReason = MissingFilesMessage(step, missing);
                    await SaveRunAsync(scope, state).ConfigureAwait(false);
                    _watches[sessionId] = StepWatch.ForReply(state.Run.Id, visit, state.Run.UserId);
                    return;
                }

                _watches.TryRemove(sessionId, out _);
                visit.FilesChecked = true;
                await CommitFilesAsync(scope, state, step, visit).ConfigureAwait(false);
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                await RouteAsync(scope, state, step, visit, checkLoop: false, checkFiles: false, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            case WorkflowWaitingKinds.WrapUpFailed:
                _watches.TryRemove(sessionId, out _);
                visit.Status = WorkflowRunStepStatus.Running;
                visit.Outcome = null;
                visit.HandOffNote = null;
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                state.Run.Status = WorkflowRunStatus.Running;
                state.Run.WaitingReason = null;
                state.Run.WaitingKind = null;
                await SaveRunAsync(scope, state).ConfigureAwait(false);
                return;
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
            // A step that stopped without the tool can still finish if the user replies to it; one that waits on a
            // missing file or a wrap-up hears when the user's reply there ends.
            if (state.Run.WaitingKind is WorkflowWaitingKinds.MissingFiles or WorkflowWaitingKinds.WrapUpFailed && visit?.SessionId is { } waiting)
                _watches[waiting] = StepWatch.ForReply(state.Run.Id, visit, state.Run.UserId);
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

            case WorkflowRunStepStatus.Running when visit.Finish == WorkflowFinishers.You:
                // A conversation with the user: the restart stopped its turn, and the user carries on by replying.
                break;

            case WorkflowRunStepStatus.WrappingUp when step is WorkflowAgentStep agent:
                await WaitAsync(scope, state, visit.StepId,
                    $"Fleet restarted during {agent.Title}'s wrap-up, which stopped it. Reply to the agent in its session, or move on anyway.",
                    WorkflowWaitingKinds.WrapUpFailed).ConfigureAwait(false);
                if (visit.SessionId is { } wrapping)
                    _watches[wrapping] = StepWatch.ForReply(state.Run.Id, visit, state.Run.UserId);
                break;

            case WorkflowRunStepStatus.Running when step is not null:
                visit.Status = WorkflowRunStepStatus.Waiting;
                await scope.Runs.UpdateStepAsync(visit).ConfigureAwait(false);
                await WaitAsync(scope, state, visit.StepId,
                    $"Fleet restarted while {step.Title} was running, which stopped its turn. Reply to the agent to carry on, or pick an outcome.")
                    .ConfigureAwait(false);
                break;

            case WorkflowRunStepStatus.Done when step is WorkflowAgentStep agent:
                await RouteAsync(scope, state, agent, visit, checkLoop: true, checkFiles: true, ct).ConfigureAwait(false);
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

    private enum WatchKind
    {
        /// <summary>A step the agent finishes: its turn ending without the tool stops the run on the user.</summary>
        Stall,

        /// <summary>The turn that answers the wrap-up prompt of a step the user moved on from.</summary>
        WrapUp,

        /// <summary>A turn the user starts in a step that waits on a missing file or a wrap-up.</summary>
        Reply,
    }

    /// <summary>A step session's turn Fleet is waiting to end.</summary>
    private sealed class StepWatch(string runId, string visitId, string userId, WatchKind kind, IReadOnlySet<string>? ownPrompts = null)
    {
        private readonly Lock _gate = new();
        private readonly List<(string Id, string? ParentId)> _early = [];
        private readonly List<(string? MessageId, string? Error)> _earlyFailures = [];
        private readonly HashSet<string> _replies = new(StringComparer.Ordinal);
        private bool _idledEarly;

        public string RunId { get; } = runId;
        public string VisitId { get; } = visitId;
        public string UserId { get; } = userId;
        public WatchKind Kind { get; } = kind;

        /// <summary>The wrap-up prompt's id, once it's sent. Its replies name it as their parent.</summary>
        public string? MessageId { get; private set; }

        /// <summary>The watched turn has started: an assistant message in it, or a failure.</summary>
        public bool Armed { get; private set; }

        public string? Failure { get; private set; }

        /// <summary>
        /// Watches for a turn the user starts: its replies name the user's message, not the step's prompt or the
        /// wrap-up, whose last events can still arrive.
        /// </summary>
        public static StepWatch ForReply(string runId, WorkflowRunStep visit, string userId)
            => new(runId, visit.Id, userId, WatchKind.Reply, new[] { visit.PromptMessageId, visit.WrapUpMessageId }.OfType<string>().ToHashSet(StringComparer.Ordinal));

        public void Saw(string id, string? parentId)
        {
            lock (_gate)
            {
                switch (Kind)
                {
                    case WatchKind.Stall:
                        Armed = true;
                        break;
                    case WatchKind.WrapUp when MessageId is null:
                        _early.Add((id, parentId));
                        break;
                    case WatchKind.WrapUp when parentId == MessageId:
                        Armed = true;
                        _replies.Add(id);
                        break;
                    case WatchKind.Reply when parentId is null || ownPrompts is null || !ownPrompts.Contains(parentId):
                        Armed = true;
                        break;
                }
            }
        }

        public void Failed(string? messageId, string? error)
        {
            lock (_gate)
            {
                switch (Kind)
                {
                    case WatchKind.Stall:
                        Armed = true;
                        Failure = error;
                        break;
                    case WatchKind.WrapUp when MessageId is null:
                        _earlyFailures.Add((messageId, error));
                        break;
                    case WatchKind.WrapUp when messageId is null ? Armed : _replies.Contains(messageId):
                        Failure = error;
                        break;
                }
            }
        }

        /// <summary>Whether this idle can end the watched turn.</summary>
        public bool Idled()
        {
            lock (_gate)
            {
                if (Kind == WatchKind.WrapUp && MessageId is null)
                {
                    _idledEarly = true;
                    return false;
                }

                return Armed;
            }
        }

        /// <summary>
        /// The wrap-up prompt went, with this id. True when what came before the send returned already ended its turn.
        /// </summary>
        public bool Sent(string messageId)
        {
            lock (_gate)
            {
                MessageId = messageId;
                foreach (var (id, parentId) in _early)
                {
                    if (parentId == messageId)
                    {
                        Armed = true;
                        _replies.Add(id);
                    }
                }

                foreach (var (failedId, error) in _earlyFailures)
                {
                    if (failedId is null ? Armed : _replies.Contains(failedId))
                        Failure = error;
                }

                return Armed && _idledEarly;
            }
        }
    }

    private sealed class Scope(IServiceScope scope, IDisposable user) : IDisposable
    {
        public IWorkflowRunRepository Runs { get; } = scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>();
        public IWorkflowStepSessions Sessions => scope.ServiceProvider.GetRequiredService<IWorkflowStepSessions>();
        public IWorkflowRunEvents Events => scope.ServiceProvider.GetRequiredService<IWorkflowRunEvents>();
        public WorkflowsFeature Feature => scope.ServiceProvider.GetRequiredService<WorkflowsFeature>();
        public IWorkflowFiles Files => scope.ServiceProvider.GetRequiredService<IWorkflowFiles>();
        public WorkflowSkills Skills => scope.ServiceProvider.GetRequiredService<WorkflowSkills>();

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: {StepId} starts without {Skill}, which is off")]
    private partial void LogWithoutSkill(string runId, string stepId, string skill);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: {StepId} decided {Choice}")]
    private partial void LogDecided(string runId, string stepId, string choice);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: the user moved on from {StepId} with {Outcome}; wrapping up")]
    private partial void LogMovingOn(string runId, string stepId, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: the user moved on from {StepId} anyway")]
    private partial void LogMovedOnAnyway(string runId, string stepId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't read the reply to {StepId}'s wrap-up")]
    private partial void LogReplyReadFailed(Exception ex, string runId, string stepId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId}: committed {StepId}'s declared files as {Commit}")]
    private partial void LogCommitted(string runId, string stepId, string commit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {RunId}: couldn't commit {StepId}'s declared files: {Error}")]
    private partial void LogCommitFailed(string runId, string stepId, string error);

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
