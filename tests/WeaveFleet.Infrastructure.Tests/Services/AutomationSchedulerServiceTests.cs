using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class AutomationSchedulerServiceTests : IDisposable
{
    // Monday 14 September 2026, 09:00 UTC.
    private static readonly DateTimeOffset NineOnMonday = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeAutomationRepository _automations = new();
    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly RecordingExecutor _executor = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly Clock _clock = new(NineOnMonday);
    private readonly AutomationSchedulerService _scheduler;

    public AutomationSchedulerServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAutomationRepository>(_automations);
        services.AddSingleton<IAutomationRunRepository>(_runs);
        services.AddSingleton<IAutomationExecutor>(_executor);
        services.AddSingleton(_activity);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<AutomationRunService>();
        var provider = services.BuildServiceProvider();

        _scheduler = new AutomationSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            NullLogger<AutomationSchedulerService>.Instance);
    }

    private Automation Seed(string triggerType, string config, string? timeZone = null, DateTimeOffset? since = null, int maxConcurrentRuns = 1)
    {
        var automation = new Automation
        {
            Id = $"auto-{_automations.ListAsync().Result.Count + 1}",
            Name = "Weekly digest",
            Prompt = "Summarise the open PRs",
            TriggerType = triggerType,
            TriggerConfig = config,
            TimeZone = timeZone,
            MaxConcurrentRuns = maxConcurrentRuns,
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = (since ?? NineOnMonday.AddDays(-7)).UtcDateTime.ToString("O"),
        };
        _automations.Seed(automation);
        return automation;
    }

    public void Dispose() => _scheduler.Dispose();

    private async Task PollAtAsync(DateTimeOffset now)
    {
        _clock.Now = now;
        await _scheduler.PollAsync(CancellationToken.None);

        // Sessions start on the thread pool after the poll; wait until every run has settled.
        for (var i = 0; i < 100 && _runs.All.Any(run => run.Status == AutomationRunStatus.Starting); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task A_schedule_that_is_due_runs_once()
    {
        var automation = Seed("schedule", "0 9 * * 1");

        await PollAtAsync(NineOnMonday.AddSeconds(15));
        await PollAtAsync(NineOnMonday.AddSeconds(45));

        _executor.Calls.ShouldBe([automation.Id]);
        var run = _runs.All.ShouldHaveSingleItem();
        (run.Trigger, run.Status, run.SessionId).ShouldBe(("schedule", "started", "session-1"));
        run.ScheduledFor.ShouldBe(NineOnMonday.UtcDateTime.ToString("O"));
    }

    [Fact]
    public async Task A_schedule_that_is_not_due_does_nothing()
    {
        Seed("schedule", "0 0 1 1 *");

        await PollAtAsync(NineOnMonday.AddSeconds(15));

        _executor.Calls.ShouldBeEmpty();
        _runs.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_schedule_runs_on_the_automations_clock()
    {
        // 11:00 in Johannesburg is 09:00 UTC; the same cron in UTC isn't due until 11:00 UTC.
        var johannesburg = Seed("schedule", "0 11 * * 1", "Africa/Johannesburg");
        Seed("schedule", "0 11 * * 1");

        await PollAtAsync(NineOnMonday.AddSeconds(15));

        _executor.Calls.ShouldBe([johannesburg.Id]);
    }

    [Fact]
    public async Task A_run_missed_by_less_than_three_hours_is_caught_up_once()
    {
        Seed("schedule", "0 9 * * 1");

        await PollAtAsync(NineOnMonday.AddHours(2));
        await PollAtAsync(NineOnMonday.AddHours(2).AddSeconds(30));

        _executor.Calls.Count.ShouldBe(1);
        _runs.All.ShouldHaveSingleItem().Trigger.ShouldBe("catch_up");
    }

    [Fact]
    public async Task A_run_missed_by_more_than_three_hours_is_recorded_as_skipped()
    {
        Seed("schedule", "0 9 * * 1");

        await PollAtAsync(NineOnMonday.AddHours(5));

        _executor.Calls.ShouldBeEmpty();
        var run = _runs.All.ShouldHaveSingleItem();
        run.Status.ShouldBe(AutomationRunStatus.Skipped);
        run.Error!.ShouldStartWith("Skipped: Fleet wasn't running at Mon 14 Sep, 09:00");
    }

    [Fact]
    public async Task Nothing_from_before_it_was_switched_on_runs()
    {
        var automation = Seed("schedule", "0 9 * * 1");
        automation.UpdatedAt = NineOnMonday.AddMinutes(30).UtcDateTime.ToString("O");

        await PollAtAsync(NineOnMonday.AddMinutes(40));

        _runs.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_one_off_runs_and_then_switches_itself_off()
    {
        var automation = Seed("once", "2026-09-14T11:00", "Africa/Johannesburg");

        await PollAtAsync(NineOnMonday.AddSeconds(10));
        await PollAtAsync(NineOnMonday.AddSeconds(40));

        _executor.Calls.ShouldBe([automation.Id]);
        _runs.All.ShouldHaveSingleItem().Trigger.ShouldBe("once");
        (await _automations.GetByIdAsync(automation.Id))!.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_one_off_missed_by_hours_is_skipped_and_switched_off()
    {
        var automation = Seed("once", "2026-09-14T09:00");

        await PollAtAsync(NineOnMonday.AddHours(6));

        _executor.Calls.ShouldBeEmpty();
        _runs.All.ShouldHaveSingleItem().Status.ShouldBe(AutomationRunStatus.Skipped);
        (await _automations.GetByIdAsync(automation.Id))!.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_is_skipped_while_the_last_one_is_still_going()
    {
        Seed("schedule", "0 * * * *");
        await PollAtAsync(NineOnMonday.AddSeconds(10));
        _activity.Update("session-1", "busy", "user-1");

        await PollAtAsync(NineOnMonday.AddHours(1).AddSeconds(10));

        _executor.Calls.Count.ShouldBe(1);
        _runs.All[^1].Status.ShouldBe(AutomationRunStatus.Skipped);
        _runs.All[^1].Error.ShouldBe("Skipped: the last run was still going.");
    }

    [Fact]
    public async Task Runs_left_starting_by_a_crash_are_marked_failed()
    {
        await _runs.InsertAsync(new AutomationRun
        {
            Id = "run-stuck",
            AutomationId = "auto-gone",
            UserId = "user-1",
            Trigger = "schedule",
            StartedAt = NineOnMonday.AddHours(-1).UtcDateTime.ToString("O"),
            Status = AutomationRunStatus.Starting,
        });

        await PollAtAsync(NineOnMonday);

        var run = _runs.All.ShouldHaveSingleItem();
        (run.Status, run.Error).ShouldBe(("failed", "Fleet stopped before the run started."));
    }

    [Fact]
    public void Handled_until_is_the_later_of_the_last_occurrence_and_when_it_was_switched_on()
    {
        var automation = new Automation
        {
            Id = "auto-1",
            CreatedAt = "2026-09-01T00:00:00.0000000Z",
            UpdatedAt = "2026-09-10T00:00:00.0000000Z",
        };

        AutomationSchedulerService.HandledUntilUtc(automation, new Dictionary<string, string>())
            .ShouldBe(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        AutomationSchedulerService.HandledUntilUtc(automation, new Dictionary<string, string> { ["auto-1"] = "2026-09-14T09:00:00.0000000Z" })
            .ShouldBe(new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingExecutor : IAutomationExecutor
    {
        private readonly List<string> _calls = [];
        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_calls)
                    return [.. _calls];
            }
        }

        public Task<AutomationExecutionOutcome> ExecuteAsync(
            Automation automation,
            string? eventType = null,
            string? eventSummary = null,
            string? previousSessionId = null,
            CancellationToken ct = default)
        {
            lock (_calls)
                _calls.Add(automation.Id);
            return Task.FromResult(new AutomationExecutionOutcome("session-1", "instance-1", null));
        }
    }
}
