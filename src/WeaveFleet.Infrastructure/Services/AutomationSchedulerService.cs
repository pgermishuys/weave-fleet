using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Runs scheduled and one-off automations. Every poll, each automation's latest occurrence since the last one it
/// recorded (or since it was switched on or changed) is run on time, caught up if Fleet was off for less than
/// <see cref="AutomationSchedule.CatchUpLimit"/>, or recorded as skipped. The record is the watermark, so a restart
/// neither repeats a run nor forgets one. A one-off automation switches itself off once its time is handled.
/// </summary>
public sealed partial class AutomationSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleStartingAfter = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutomationSchedulerService> _logger;
    private bool _hasCleanedUp;

    public AutomationSchedulerService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<AutomationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogSchedulerPollError(ex);
            }

            await Task.Delay(PollInterval, _timeProvider, stoppingToken);
        }
    }

    /// <summary>
    /// One pass over the timed automations. Runs are recorded before this returns and their sessions start in the
    /// background, so the next poll sees them.
    /// </summary>
    internal async Task PollAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var automations = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
        var runs = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
        var runService = scope.ServiceProvider.GetRequiredService<AutomationRunService>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (!_hasCleanedUp)
        {
            // Runs Fleet was starting when it stopped will never finish starting.
            await runs.FailStaleStartingAsync((now - StaleStartingAfter).ToString("O"), "Fleet stopped before the run started.");
            _hasCleanedUp = true;
        }

        var due = new List<Automation>();
        due.AddRange(await automations.ListEnabledByTriggerTypeAsync(AutomationSchedule.ScheduleTrigger));
        due.AddRange(await automations.ListEnabledByTriggerTypeAsync(AutomationSchedule.OnceTrigger));
        var lastScheduled = await runs.GetLastScheduledForAsync();

        foreach (var automation in due)
        {
            try
            {
                var decision = AutomationSchedule.Decide(automation, HandledUntilUtc(automation, lastScheduled), now);
                if (decision is null)
                    continue;

                await HandleAsync(automation, decision, runs, runService, automations, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAutomationFailed(ex, automation.Id);
            }
        }
    }

    private async Task HandleAsync(
        Automation automation,
        ScheduleDecision decision,
        IAutomationRunRepository runs,
        AutomationRunService runService,
        IAutomationRepository automations,
        CancellationToken ct)
    {
        var isOnce = AutomationSchedule.IsOnce(automation.TriggerType);

        if (decision.Kind == ScheduleDecisionKind.Missed)
        {
            LogMissed(automation.Id, decision.OccurrenceUtc);
            await runs.InsertAsync(new AutomationRun
            {
                Id = Ulid.NewUlid().ToString(),
                AutomationId = automation.Id,
                UserId = automation.UserId,
                Trigger = isOnce ? AutomationSchedule.OnceTrigger : AutomationSchedule.ScheduleTrigger,
                ScheduledFor = decision.OccurrenceUtc.ToString("O"),
                StartedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O"),
                Status = AutomationRunStatus.Skipped,
                Error = AutomationSchedule.DescribeMissed(automation, decision.OccurrenceUtc),
            });
        }
        else
        {
            var trigger = new AutomationRunTrigger(
                decision.Kind == ScheduleDecisionKind.CatchUp ? "catch_up" : isOnce ? AutomationSchedule.OnceTrigger : AutomationSchedule.ScheduleTrigger,
                decision.OccurrenceUtc);
            var run = await runService.BeginAsync(automation, trigger);
            if (run.Status == AutomationRunStatus.Starting)
            {
                LogStarting(automation.Id, trigger.Name, decision.OccurrenceUtc);
                _ = Task.Run(() => FinishInOwnScopeAsync(automation, run, trigger, ct), ct);
            }
        }

        // Its one time has been handled, whichever way.
        if (isOnce)
            await automations.SetEnabledAsync(automation.Id, false);
    }

    private async Task FinishInOwnScopeAsync(Automation automation, AutomationRun run, AutomationRunTrigger trigger, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runService = scope.ServiceProvider.GetRequiredService<AutomationRunService>();
            await runService.FinishAsync(automation, run, trigger, ct);
        }
        catch (Exception ex)
        {
            LogExecutionFailed(ex, automation.Id);
        }
    }

    /// <summary>
    /// The moment up to which an automation's occurrences are dealt with: its last recorded occurrence, or when it
    /// was last switched on or changed, whichever is later. Nothing before it runs.
    /// </summary>
    internal static DateTime HandledUntilUtc(Automation automation, IReadOnlyDictionary<string, string> lastScheduled)
    {
        var since = ParseUtc(automation.UpdatedAt) ?? ParseUtc(automation.CreatedAt) ?? DateTime.MinValue;
        var last = lastScheduled.TryGetValue(automation.Id, out var value) ? ParseUtc(value) : null;
        return last > since ? last.Value : since;
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in automation scheduler poll")]
    private partial void LogSchedulerPollError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automation {AutomationId} could not be scheduled")]
    private partial void LogAutomationFailed(Exception ex, string automationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation {AutomationId} run ({Trigger}) for {OccurrenceUtc:O}")]
    private partial void LogStarting(string automationId, string trigger, DateTime occurrenceUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation {AutomationId} missed its run at {OccurrenceUtc:O}; recorded as skipped")]
    private partial void LogMissed(string automationId, DateTime occurrenceUtc);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute scheduled automation {AutomationId}")]
    private partial void LogExecutionFailed(Exception ex, string automationId);
}
