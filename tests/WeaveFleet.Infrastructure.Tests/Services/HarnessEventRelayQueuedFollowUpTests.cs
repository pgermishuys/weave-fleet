using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// A turn that ends with a message queued carries straight on: the relay doesn't show the session idle or tell the user
/// it finished before the queued message runs. If that message can't be sent, the idle comes as before.
/// </summary>
public sealed class HarnessEventRelayQueuedFollowUpTests
{
    private const string User = "user-1";

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeHarnessSession Harness = new("inst-1");
        public readonly SessionOrchestratorBuilder Builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext(User));
        public readonly InMemoryQueuedPromptRepository Queue = new();
        public readonly FakeEventBroadcaster Broadcaster = new();
        public readonly HarnessEventRelay Relay;
        public readonly PromptQueueService Service;

        public Rig(string retention = "active")
        {
            Builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities());
            Builder.SessionRepository.Seed(new Session
            {
                Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active", Directory = "/work/demo", CreatedAt = "2026-01-01",
                RetentionStatus = retention, HarnessType = "opencode", UserId = User,
            });
            Builder.InstanceTracker.Register("inst-1", Harness);
            var orchestrator = Builder.Build();
            Service = new PromptQueueService(
                Builder.Prompting, Builder.ShellCommands, Builder.SessionRepository, Queue, Builder.EventBroadcaster,
                Builder.ActivityTracker, Builder.HarnessRegistry, new TestUserContext(User),
                new TurnRetryScheduler(TestServiceScopeFactory.CreateEmpty(), TimeProvider.System, NullLogger<TurnRetryScheduler>.Instance),
                NullLogger<PromptQueueService>.Instance);
            var scopes = TestServiceScopeFactory.Create(services =>
            {
                services.AddLogging();
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton(orchestrator);
                services.AddSingleton<ISessionRepository>(Builder.SessionRepository);
                services.AddSingleton(Service);
                services.AddSingleton(new SessionCapabilitiesResolver(Builder.InstanceTracker, Builder.ActivityTracker));
                services.AddTransient<DomainEventTranslator>();
            });
            var notifier = new SessionNotifier(
                new SessionFocusTracker(), [new BroadcastNotificationSink(Broadcaster)], scopes, NullLogger<SessionNotifier>.Instance);
            Relay = new HarnessEventRelay(
                Builder.InstanceTracker, Broadcaster, new FakeEventPublisher(), Builder.ActivityTracker, scopes,
                NullLogger<HarnessEventRelay>.Instance, notifier: notifier,
                queue: new PromptQueueDispatcher(scopes, NullLogger<PromptQueueDispatcher>.Instance));
        }

        public List<string> Shown => [.. Broadcaster.Broadcasts
            .Where(b => b.Topic == "sessions" && b.Type == "activity_status")
            .Select(b => b.Payload.GetProperty("activityStatus").GetString()!)];

        public int Finished => Broadcaster.Broadcasts.Count(b => b.Type == SessionNotifier.EventType);

        public static async Task UntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
                await Task.Delay(10);
            }
        }

        private void EmitStatus(string status) => Harness.Emit(new HarnessEvent
        {
            Type = status == ActivityStatuses.Idle ? EventTypes.SessionIdle : EventTypes.SessionStatus,
            SessionId = "oc-s1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = status } }),
        });

        /// <summary>The relay subscribes in the background: re-send busy until it has been shown, so no fixed wait is needed.</summary>
        public async Task BusyAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!Shown.Contains(ActivityStatuses.Busy))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for the busy status to be shown");
                EmitStatus(ActivityStatuses.Busy);
                await Task.Delay(25);
            }
        }

        public void Idle() => EmitStatus(ActivityStatuses.Idle);

        /// <summary>How OpenCode ends a turn: a status event saying idle, then the idle event itself.</summary>
        public void IdleTwice()
        {
            Harness.Emit(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = "oc-s1",
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new { status = new { type = ActivityStatuses.Idle } }),
            });
            Idle();
        }

        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    [Fact]
    public async Task A_turn_that_ends_with_a_message_queued_is_not_shown_idle_or_finished()
    {
        await using var rig = new Rig();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await rig.Relay.StartAsync(cts.Token);
        await rig.BusyAsync();
        await rig.Service.EnqueueAsync("s1", new QueuePromptRequest("next", QueuedPromptKinds.Prompt));
        var before = rig.Shown.Count;

        rig.Idle();
        await Rig.UntilAsync(() => rig.Harness.SendPromptCalls.Count > 0, "the queued message to be sent");
        await Task.Delay(300); // a negative check below: a late idle or notice would be caught here, a slow runner can't fail it

        rig.Harness.SendPromptCalls.Select(c => c.Text).ShouldBe(["next"]);
        rig.Shown.Skip(before).ShouldNotContain(ActivityStatuses.Idle);
        rig.Finished.ShouldBe(0);

        await cts.CancelAsync();
        await rig.Relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_turn_that_ends_with_two_idle_events_and_a_message_queued_is_not_shown_idle_or_finished()
    {
        await using var rig = new Rig();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await rig.Relay.StartAsync(cts.Token);
        await rig.BusyAsync();
        await rig.Service.EnqueueAsync("s1", new QueuePromptRequest("next", QueuedPromptKinds.Prompt));
        var before = rig.Shown.Count;

        rig.IdleTwice();
        await Rig.UntilAsync(() => rig.Harness.SendPromptCalls.Count > 0, "the queued message to be sent");
        await Task.Delay(300);

        rig.Shown.Skip(before).ShouldNotContain(ActivityStatuses.Idle);
        rig.Finished.ShouldBe(0);

        await cts.CancelAsync();
        await rig.Relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_queued_message_the_session_refuses_shows_the_idle_after_all()
    {
        await using var rig = new Rig(retention: "archived");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await rig.Relay.StartAsync(cts.Token);
        await rig.BusyAsync();
        await rig.Queue.AddAsync(new QueuedPrompt { Id = "q1", SessionId = "s1", Kind = QueuedPromptKinds.Prompt, Text = "next", CreatedAt = DateTimeOffset.UtcNow });

        rig.Idle();
        await Rig.UntilAsync(() => rig.Shown[^1] == ActivityStatuses.Idle, "the idle to be shown");

        rig.Harness.SendPromptCalls.ShouldBeEmpty();
        // (The notifier says nothing about an archived session, so the idle is the signal here.)
        rig.Shown[^1].ShouldBe(ActivityStatuses.Idle);

        await cts.CancelAsync();
        await rig.Relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_turn_that_ends_with_nothing_queued_is_shown_idle_and_finished()
    {
        await using var rig = new Rig();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await rig.Relay.StartAsync(cts.Token);
        await rig.BusyAsync();

        rig.Idle();
        await Rig.UntilAsync(() => rig.Finished == 1 && rig.Shown[^1] == ActivityStatuses.Idle, "the idle and the finish notice");

        rig.Shown[^1].ShouldBe(ActivityStatuses.Idle);
        rig.Finished.ShouldBe(1);

        await cts.CancelAsync();
        await rig.Relay.StopAsync(CancellationToken.None);
    }
}
