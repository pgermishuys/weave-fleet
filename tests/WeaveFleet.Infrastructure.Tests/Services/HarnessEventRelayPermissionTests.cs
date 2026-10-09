using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }
}
