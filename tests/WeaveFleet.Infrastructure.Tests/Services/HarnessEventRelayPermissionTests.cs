using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Sessions;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay keeps the asks waiting on the user and shows each where the user looks: a subagent's on the session it
/// works for. When a harness goes away, its asks go with it.
/// </summary>
public sealed class HarnessEventRelayPermissionTests
{
    private const string Parent = "fleet-parent";
    private const string Child = "fleet-child";

    [Fact]
    public async Task A_subagents_ask_is_shown_on_the_session_it_works_for_and_forgotten_when_its_harness_goes()
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = Parent, InstanceId = "instance-parent", UserId = "user-1", HarnessType = "opencode2" });
        sessions.Seed(new Session { Id = Child, InstanceId = "instance-child", UserId = "user-1", HarnessType = "opencode2", ParentSessionId = Parent });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessions);
            services.AddTransient<DomainEventTranslator>();
        });
        var tracker = new InstanceTracker();
        var broadcaster = new FakeEventBroadcaster();
        var publisher = new FakeEventPublisher();
        var store = new PendingPermissionStore();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, publisher, new SessionActivityTracker(), scopeFactory, NullLogger<HarnessEventRelay>.Instance,
            permissions: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);

        var child = new FakeHarnessSession("instance-child");
        tracker.Register(child.InstanceId, child);
        var ask = new PermissionAsk { Id = "per_1", SessionId = Child, Kind = PermissionKinds.Shell, Tool = "shell", Title = "npm test" };
        child.Emit(PermissionEvents.Asked(ask, "ses_child"));

        await WaitUntilAsync(() => publisher.Calls.Any(c => c.Event.Type == EventTypes.PermissionAsked), cts.Token);
        publisher.Calls.Single(c => c.Event.Type == EventTypes.PermissionAsked).Context.FleetSessionId.ShouldBe(Parent);
        store.WaitingIn(Parent).ShouldHaveSingleItem().Id.ShouldBe("per_1");
        store.WaitingIn(Child).ShouldBeEmpty();

        child.Complete();

        await WaitUntilAsync(() => broadcaster.Broadcasts.Any(b => b.Type == EventTypes.PermissionReplied), cts.Token);
        var gone = broadcaster.Broadcasts.Single(b => b.Type == EventTypes.PermissionReplied);
        gone.Topic.ShouldBe($"session:{Parent}");
        gone.Payload.GetProperty("reply").GetString().ShouldBe(PermissionReplies.Gone);
        store.WaitingIn(Parent).ShouldBeEmpty();

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_answered_ask_no_longer_waits()
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = Parent, InstanceId = "instance-parent", UserId = "user-1", HarnessType = "claude-code" });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessions);
            services.AddTransient<DomainEventTranslator>();
        });
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var store = new PendingPermissionStore();
        var relay = new HarnessEventRelay(
            tracker, new FakeEventBroadcaster(), publisher, new SessionActivityTracker(), scopeFactory, NullLogger<HarnessEventRelay>.Instance,
            permissions: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        var session = new FakeHarnessSession("instance-parent");
        tracker.Register(session.InstanceId, session);
        var ask = new PermissionAsk { Id = "req_1", SessionId = Parent, Kind = PermissionKinds.Edit, Tool = "Edit" };

        session.Emit(PermissionEvents.Asked(ask, Parent));
        session.Emit(PermissionEvents.Replied(ask, PermissionReplies.Once, Parent));

        await WaitUntilAsync(() => publisher.Calls.Any(c => c.Event.Type == EventTypes.PermissionReplied), cts.Token);
        store.WaitingIn(Parent).ShouldBeEmpty();

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_ask_a_mod_refuses_is_never_shown_and_is_answered_with_the_mods_words()
    {
        var mods = new FakeModPermissions { Refuse = ask => ask.Id == "per_deny" ? "push-guard: Don't push from main." : null };
        var (relay, session, publisher, store) = Start(mods);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        var denied = new PermissionAsk { Id = "per_deny", SessionId = Parent, Kind = PermissionKinds.Shell, Tool = "bash", Title = "git push" };
        var shown = new PermissionAsk { Id = "per_ok", SessionId = Parent, Kind = PermissionKinds.Shell, Tool = "bash", Title = "ls" };

        // What an adapter sends: the ask and its Needs-you, then (once our answer is in) the harness's own settling.
        session.Emit(PermissionEvents.Asked(denied, Parent));
        session.Emit(PermissionEvents.Status(ActivityStatuses.WaitingInput, Parent));
        session.Emit(PermissionEvents.Replied(denied, PermissionReplies.Reject, Parent));
        session.Emit(PermissionEvents.Status(ActivityStatuses.Busy, Parent));
        session.Emit(PermissionEvents.Asked(shown, Parent));

        await WaitUntilAsync(() => publisher.Calls.Any(c => c.Event.Type == EventTypes.PermissionAsked), cts.Token);
        (await mods.Rejected.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(("per_deny", "push-guard: Don't push from main."));
        publisher.Calls.Count(c => c.Event.Type == EventTypes.PermissionAsked).ShouldBe(1);
        publisher.Calls.Single(c => c.Event.Type == EventTypes.PermissionAsked).Event.Payload!.Value.GetProperty("id").GetString().ShouldBe("per_ok");
        publisher.Calls.ShouldNotContain(c => c.Event.Type == EventTypes.PermissionReplied, "the harness's settling of a refused ask is dropped");
        store.WaitingIn(Parent).ShouldHaveSingleItem().Id.ShouldBe("per_ok");
        var statuses = publisher.Calls.Where(c => c.Event.Type == EventTypes.SessionStatus).Select(c => c.Event.Payload!.Value.GetProperty("status").GetProperty("type").GetString()).ToList();
        statuses.ShouldBe([ActivityStatuses.Busy], "the Needs-you for the refused ask is dropped");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_ask_no_mod_refuses_is_recorded_and_published_as_before()
    {
        var mods = new FakeModPermissions();
        var (relay, session, publisher, store) = Start(mods);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        var ask = new PermissionAsk { Id = "per_1", SessionId = Parent, Kind = PermissionKinds.Shell, Tool = "bash", Title = "git push" };

        session.Emit(PermissionEvents.Asked(ask, Parent));
        session.Emit(PermissionEvents.Status(ActivityStatuses.WaitingInput, Parent));

        await WaitUntilAsync(() => publisher.Calls.Any(c => c.Event.Type == EventTypes.SessionStatus), cts.Token);
        publisher.Calls.ShouldContain(c => c.Event.Type == EventTypes.PermissionAsked);
        store.WaitingIn(Parent).ShouldHaveSingleItem().Id.ShouldBe("per_1");
        mods.Asked.ShouldBe([("user-1", Parent, "per_1")]);
        mods.Rejected.Task.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    private static (HarnessEventRelay Relay, FakeHarnessSession Session, FakeEventPublisher Publisher, PendingPermissionStore Store) Start(IModPermissionHook mods)
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = Parent, InstanceId = "instance-parent", UserId = "user-1", HarnessType = "opencode" });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessions);
            services.AddTransient<DomainEventTranslator>();
        });
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var store = new PendingPermissionStore();
        var relay = new HarnessEventRelay(
            tracker, new FakeEventBroadcaster(), publisher, new SessionActivityTracker(), scopeFactory, NullLogger<HarnessEventRelay>.Instance,
            permissions: store, modPermissions: mods);
        var session = new FakeHarnessSession("instance-parent");
        tracker.Register(session.InstanceId, session);
        return (relay, session, publisher, store);
    }

    private sealed class FakeModPermissions : IModPermissionHook
    {
        public Func<PermissionAsk, string?> Refuse { get; init; } = _ => null;

        public List<(string User, string ShownOn, string Id)> Asked { get; } = [];

        public TaskCompletionSource<(string Id, string Message)> Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> RefusalAsync(string userId, string shownOn, PermissionAsk ask, CancellationToken ct)
        {
            Asked.Add((userId, shownOn, ask.Id));
            return Task.FromResult(Refuse(ask));
        }

        public Task RejectAsync(string userId, PermissionAsk ask, string message)
        {
            Rejected.TrySetResult((ask.Id, message));
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }
}
