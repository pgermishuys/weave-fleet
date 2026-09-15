using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>What started a run.</summary>
/// <param name="Name">"schedule", "catch_up", "once", "manual", or the event type.</param>
/// <param name="ScheduledForUtc">The schedule occurrence it's for; null for Run now and events.</param>
public sealed record AutomationRunTrigger(
    string Name,
    DateTime? ScheduledForUtc = null,
    string? EventType = null,
    string? EventSummary = null)
{
    public const string Manual = "manual";
    public bool IsManual => Name == Manual;
}

/// <summary>What the runs list says about a run.</summary>
public static class AutomationRunState
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>
/// The one way an automation runs, whatever started it. Each run gets a row in <c>automation_runs</c>: started with
/// its session, failed with the reason, or skipped because the last run was still going.
/// </summary>
public sealed partial class AutomationRunService(
    IAutomationRunRepository runRepository,
    IAutomationExecutor executionService,
    SessionActivityTracker activityTracker,
    TimeProvider timeProvider,
    ILogger<AutomationRunService> logger)
{
    /// <summary>A run still "starting" after this is assumed stuck, so it no longer holds up the next one.</summary>
    private static readonly TimeSpan StartingTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Records a run before it starts, so the runs list shows it at once. When "skip while the last run is going" is
    /// on and it is, the run is recorded as skipped instead. Run now is never skipped: someone asked for it.
    /// </summary>
    public async Task<AutomationRun> BeginAsync(Automation automation, AutomationRunTrigger trigger)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var run = new AutomationRun
        {
            Id = Ulid.NewUlid().ToString(),
            AutomationId = automation.Id,
            UserId = automation.UserId,
            Trigger = trigger.Name,
            ScheduledFor = trigger.ScheduledForUtc?.ToString("O"),
            StartedAt = now.ToString("O"),
            Status = AutomationRunStatus.Starting,
        };

        if (!trigger.IsManual && await IsStillGoingAsync(automation, now))
        {
            run.Status = AutomationRunStatus.Skipped;
            run.Error = "Skipped: the last run was still going.";
            LogSkippedStillGoing(automation.Id, automation.Name);
        }

        await runRepository.InsertAsync(run);
        return run;
    }

    /// <summary>Starts the session for a run <see cref="BeginAsync"/> recorded, and writes down how it went.</summary>
    public async Task<AutomationRun> FinishAsync(Automation automation, AutomationRun run, AutomationRunTrigger trigger, CancellationToken ct = default)
    {
        if (run.Status != AutomationRunStatus.Starting)
            return run;

        var previousSessionId = automation.TargetType == "same_session"
            ? await PreviousSessionIdAsync(automation.Id, run.Id)
            : null;

        // Run now sends the prompt as a scheduled run would; "manual" only marks where the session came from.
        var outcome = await executionService.ExecuteAsync(
            automation,
            eventType: trigger.EventType ?? (trigger.IsManual ? "manual" : null),
            eventSummary: trigger.EventSummary,
            previousSessionId,
            ct);

        run.Status = outcome.SessionId is not null ? AutomationRunStatus.Started : AutomationRunStatus.Failed;
        run.SessionId = outcome.SessionId;
        run.InstanceId = outcome.InstanceId;
        run.Error = outcome.Error;
        await runRepository.CompleteAsync(run.Id, run.Status, run.SessionId, run.InstanceId, run.Error);
        return run;
    }

    /// <summary><see cref="BeginAsync"/>, then <see cref="FinishAsync"/> unless the run was skipped.</summary>
    public async Task<AutomationRun> RunAsync(Automation automation, AutomationRunTrigger trigger, CancellationToken ct = default)
    {
        var run = await BeginAsync(automation, trigger);
        return await FinishAsync(automation, run, trigger, ct);
    }

    /// <summary>What the runs list says about a run: a started run is running while its session is busy.</summary>
    public string StateOf(AutomationRun run) => run.Status switch
    {
        AutomationRunStatus.Starting => AutomationRunState.Starting,
        AutomationRunStatus.Failed => AutomationRunState.Failed,
        AutomationRunStatus.Skipped => AutomationRunState.Skipped,
        _ => run.SessionId is not null && activityTracker.GetEffectiveActivityStatus(run.SessionId) == "busy"
            ? AutomationRunState.Running
            : AutomationRunState.Done,
    };

    private async Task<bool> IsStillGoingAsync(Automation automation, DateTime nowUtc)
    {
        if (automation.MaxConcurrentRuns <= 0)
            return false;

        var recent = await runRepository.ListByAutomationAsync(automation.Id, limit: 20);
        var going = recent.Count(run => run.Status switch
        {
            AutomationRunStatus.Starting => DateTime.TryParse(run.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && nowUtc - at.ToUniversalTime() < StartingTimeout,
            AutomationRunStatus.Started => StateOf(run) == AutomationRunState.Running,
            _ => false,
        });
        return going >= automation.MaxConcurrentRuns;
    }

    private async Task<string?> PreviousSessionIdAsync(string automationId, string currentRunId)
    {
        var recent = await runRepository.ListByAutomationAsync(automationId, limit: 20);
        return recent.FirstOrDefault(run => run.Id != currentRunId && run.Status == AutomationRunStatus.Started && run.SessionId is not null)?.SessionId;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation {AutomationId} ({AutomationName}) skipped: its last run is still going")]
    private partial void LogSkippedStillGoing(string automationId, string automationName);
}
