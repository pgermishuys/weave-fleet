using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A turn a model provider's limit stopped is tried again by Fleet, by itself: when the limit resets if the provider
/// said, after a growing wait if not. The retry is kept in the database, holds the session's queue while it waits, and
/// gives way to a turn the user starts.
/// </summary>
public sealed class TurnRetryServiceTests : IAsyncDisposable
{
    private const string UserId = "user-1";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-07T01:51:43Z", CultureInfo.InvariantCulture);

    private readonly TestUserContext _user = new(UserId);
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly InMemoryQueuedPromptRepository _queue = new();
    private readonly InMemoryScheduledRetryRepository _retries = new();
    private readonly FakeTimeProvider _time = new(Start);
    private TurnRetryScheduler? _scheduler;
    private IServiceScopeFactory? _scopes;

    public TurnRetryServiceTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(_user);
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsSteering = true });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active",
            HarnessType = "opencode",
            UserId = UserId,
        });
        _builder.InstanceTracker.Register("inst-1", _harness);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private TurnRetryScheduler Scheduler()
    {
        if (_scheduler is not null)
            return _scheduler;

        var orchestrator = _builder.Build();
        TurnRetryScheduler? scheduler = null;
        _scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
            services.AddSingleton(orchestrator);
            services.AddSingleton<ISessionRepository>(_builder.SessionRepository);
            services.AddSingleton<IScheduledRetryRepository>(_retries);
            services.AddSingleton<IUserPreferenceRepository>(_builder.UserPreferenceRepository);
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton(_ => scheduler!);
            services.AddScoped(_ => new PromptQueueService(
                orchestrator,
                _builder.SessionRepository,
                _queue,
                _builder.EventBroadcaster,
                _builder.ActivityTracker,
                _builder.HarnessRegistry,
                _user,
                scheduler!,
                NullLogger<PromptQueueService>.Instance));
            services.AddScoped(sp => new TurnRetryService(
                orchestrator,
                sp.GetRequiredService<PromptQueueService>(),
                _builder.SessionRepository,
                _retries,
                _builder.UserPreferenceRepository,
                _builder.EventBroadcaster,
                _builder.ActivityTracker,
                scheduler!,
                _user,
                _time,
                NullLogger<TurnRetryService>.Instance));
        });
        scheduler = new TurnRetryScheduler(_scopes, _time, NullLogger<TurnRetryScheduler>.Instance);
        return _scheduler = scheduler;
    }

    private T Resolve<T>() where T : notnull
    {
        Scheduler();
        return _scopes!.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    private static TurnFailed Failed(string? kind, DateTimeOffset? retryAt = null, string message = "You've hit your session limit")
        => new() { Payload = new TurnFailedPayload { SessionId = "s1", Error = new TurnError { Name = "APIError", Message = message, Kind = kind, RetryAt = retryAt } } };

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = "s1" } };

    private static TurnStarted Started() => new() { Payload = new TurnStartedPayload { SessionId = "s1", MessageId = "m", Index = 0 } };

    /// <summary>A turn a limit stopped: its failure, then the idle that ends it, as the relay hands them on.</summary>
    private async Task LimitStopsTheTurnAsync(string kind = TurnErrorKinds.UsageLimit, DateTimeOffset? retryAt = null)
    {
        var scheduler = Scheduler();
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, UserId);
        scheduler.Observe("s1", UserId, Failed(kind, retryAt));
        scheduler.Observe("s1", UserId, Idled());
        await scheduler.Pending;
    }

    private async Task<int> SentAfterAsync(TimeSpan wait)
    {
        _time.Advance(wait);
        Scheduler().SendDue();
        await Scheduler().Pending;
        return _harness.SendPromptCalls.Count;
    }

    [Fact]
    public async Task A_usage_limit_is_tried_again_half_a_minute_after_it_resets()
    {
        var reset = Start.AddHours(2);
        await LimitStopsTheTurnAsync(retryAt: reset);

        var retry = _retries.All.ShouldHaveSingleItem();
        retry.DueAt.ShouldBe(reset + TurnRetryPolicy.AfterReset);
        retry.Attempt.ShouldBe(1);
        retry.ProviderSaid.ShouldBeTrue();
        _harness.SendPromptCalls.ShouldBeEmpty();

        (await SentAfterAsync(TimeSpan.FromHours(2))).ShouldBe(0);
        (await SentAfterAsync(TimeSpan.FromSeconds(31))).ShouldBe(1);
        _harness.SendPromptCalls.Single().Text.ShouldBe(TurnRetryService.ContinueText);
    }

    [Fact]
    public async Task Every_client_hears_when_it_goes_on_the_session_and_in_the_list()
    {
        await LimitStopsTheTurnAsync(retryAt: Start.AddHours(1));

        var broadcasts = _builder.EventBroadcaster.Broadcasts.Where(b => b.Type == TurnRetryService.ChangedEvent).ToList();
        broadcasts.Select(b => b.Topic).ShouldBe(["session:s1", "sessions"]);
        broadcasts.ShouldAllBe(b => b.UserId == UserId);
        var retry = broadcasts[0].Payload.GetProperty("retry");
        retry.GetProperty("kind").GetString().ShouldBe(TurnErrorKinds.UsageLimit);
        retry.GetProperty("attempt").GetInt32().ShouldBe(1);
        DateTimeOffset.Parse(retry.GetProperty("dueAt").GetString()!, CultureInfo.InvariantCulture).ShouldBe(Start.AddHours(1) + TurnRetryPolicy.AfterReset);
    }

    [Fact]
    public async Task Without_a_reset_time_the_wait_grows_with_each_attempt()
    {
        await LimitStopsTheTurnAsync(TurnErrorKinds.RateLimit);
        _retries.All.Single().DueAt.ShouldBe(Start.AddMinutes(1));

        (await SentAfterAsync(TimeSpan.FromMinutes(1))).ShouldBe(1);

        // The retry's turn is stopped by the limit again: the second attempt waits longer.
        var scheduler = Scheduler();
        scheduler.Observe("s1", UserId, Started());
        await LimitStopsTheTurnAsync(TurnErrorKinds.RateLimit);
        var second = _retries.All.ShouldHaveSingleItem();
        second.Attempt.ShouldBe(2);
        second.DueAt.ShouldBe(_time.GetUtcNow().AddMinutes(2));
    }

    [Fact]
    public async Task A_turn_that_ends_well_starts_the_attempts_over()
    {
        await LimitStopsTheTurnAsync(TurnErrorKinds.RateLimit);
        await SentAfterAsync(TimeSpan.FromMinutes(1));
        var scheduler = Scheduler();
        scheduler.Observe("s1", UserId, Started());
        scheduler.Observe("s1", UserId, Idled());
        await scheduler.Pending;

        _retries.All.ShouldBeEmpty();
        await LimitStopsTheTurnAsync(TurnErrorKinds.RateLimit);
        _retries.All.Single().Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task After_the_last_attempt_Fleet_leaves_it_to_the_user()
    {
        await _retries.SaveAsync(new ScheduledRetry
        {
            SessionId = "s1",
            UserId = UserId,
            DueAt = Start,
            Attempt = TurnRetryPolicy.MaxAttempts,
            Kind = TurnErrorKinds.RateLimit,
            Reason = "Too many requests",
            State = ScheduledRetryStates.Sent,
        });

        await LimitStopsTheTurnAsync(TurnErrorKinds.RateLimit);

        _retries.All.ShouldBeEmpty();
        Scheduler().IsHolding("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task Other_failures_arent_tried_again()
    {
        var scheduler = Scheduler();
        scheduler.Observe("s1", UserId, Failed(kind: null, message: "Invalid API key"));
        scheduler.Observe("s1", UserId, Idled());
        await scheduler.Pending;

        _retries.All.ShouldBeEmpty();
        scheduler.IsHolding("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task The_queue_waits_for_the_retry_and_goes_after_it()
    {
        var queue = Resolve<PromptQueueService>();
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, UserId);
        await queue.EnqueueAsync("s1", new QueuePromptRequest("and then the docs", QueuedPromptKinds.Prompt));

        await LimitStopsTheTurnAsync(retryAt: Start.AddMinutes(10));
        // What PromptQueueDispatcher does on the idle after the failure.
        await Resolve<PromptQueueService>().SendNextAsync("s1");

        _harness.SendPromptCalls.ShouldBeEmpty();
        (await _queue.ListAsync("s1")).ShouldHaveSingleItem();

        await SentAfterAsync(TimeSpan.FromMinutes(11));
        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe([TurnRetryService.ContinueText]);

        // The retry's turn ends: the queue goes on.
        Scheduler().IsHolding("s1").ShouldBeFalse();
        await Resolve<PromptQueueService>().SendNextAsync("s1");
        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe([TurnRetryService.ContinueText, "and then the docs"], ignoreOrder: true);
    }

    [Fact]
    public async Task Dont_retry_hands_the_session_back_and_sends_what_was_queued()
    {
        var queue = Resolve<PromptQueueService>();
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, UserId);
        await queue.EnqueueAsync("s1", new QueuePromptRequest("and then the docs", QueuedPromptKinds.Prompt));
        await LimitStopsTheTurnAsync(retryAt: Start.AddMinutes(10));

        var cancelled = await Resolve<TurnRetryService>().CancelAsync("s1");

        cancelled.IsSuccess.ShouldBeTrue();
        _retries.All.ShouldBeEmpty();
        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe(["and then the docs"]);
        (await SentAfterAsync(TimeSpan.FromMinutes(11))).ShouldBe(1);
        _builder.EventBroadcaster.Broadcasts.Last(b => b.Type == TurnRetryService.ChangedEvent)
            .Payload.GetProperty("retry").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Try_now_sends_it_at_once_and_only_once()
    {
        await LimitStopsTheTurnAsync(retryAt: Start.AddHours(3));

        (await Resolve<TurnRetryService>().SendNowAsync("s1")).IsSuccess.ShouldBeTrue();

        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe([TurnRetryService.ContinueText]);
        (await SentAfterAsync(TimeSpan.FromHours(4))).ShouldBe(1);
    }

    [Fact]
    public async Task A_turn_the_user_starts_replaces_the_retry()
    {
        await LimitStopsTheTurnAsync(retryAt: Start.AddHours(1));

        var scheduler = Scheduler();
        scheduler.Observe("s1", UserId, Started());
        await scheduler.Pending;

        _retries.All.ShouldBeEmpty();
        (await SentAfterAsync(TimeSpan.FromHours(2))).ShouldBe(0);
    }

    [Fact]
    public async Task Turned_off_in_Settings_nothing_is_scheduled()
    {
        await _builder.UserPreferenceRepository.SetAsync(TurnRetryService.PreferenceKey, "false");

        await LimitStopsTheTurnAsync(retryAt: Start.AddHours(1));

        _retries.All.ShouldBeEmpty();
        Scheduler().IsHolding("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task A_retry_waiting_when_Fleet_restarts_still_goes()
    {
        await _retries.SaveAsync(new ScheduledRetry
        {
            SessionId = "s1",
            UserId = UserId,
            DueAt = Start.AddMinutes(5),
            Attempt = 1,
            Kind = TurnErrorKinds.UsageLimit,
            Reason = "You've hit your session limit",
            ProviderSaid = true,
            State = ScheduledRetryStates.Waiting,
        });
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, UserId);

        var scheduler = Scheduler();
        using var stopping = new CancellationTokenSource();
        await scheduler.StartAsync(stopping.Token);
        try
        {
            // It loads what was waiting as it starts.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!scheduler.IsHolding("s1") && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            scheduler.IsHolding("s1").ShouldBeTrue();
            (await SentAfterAsync(TimeSpan.FromMinutes(6))).ShouldBe(1);
        }
        finally
        {
            await stopping.CancelAsync();
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_due_retry_waits_while_the_session_is_working()
    {
        await LimitStopsTheTurnAsync(retryAt: Start.AddMinutes(1));
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, UserId);

        (await SentAfterAsync(TimeSpan.FromMinutes(2))).ShouldBe(0);

        _retries.All.Single().DueAt.ShouldBe(_time.GetUtcNow().AddMinutes(1));
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, UserId);
        (await SentAfterAsync(TimeSpan.FromMinutes(1))).ShouldBe(1);
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
