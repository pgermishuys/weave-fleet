using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Progress;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Progress;

public sealed class SessionProgressServiceTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static ObservedProgressEvent Todos(string sessionId, string userId, params (string Content, string Status)[] items)
        => new(
            sessionId,
            userId,
            new TodosReported
            {
                Payload = new TodosReportedPayload
                {
                    SessionId = sessionId,
                    Items = [.. items.Select(item => new TodoEntry { Content = item.Content, Status = item.Status })],
                },
            },
            new DateTimeOffset(2026, 9, 13, 12, 24, 0, TimeSpan.Zero));

    private static SessionProgressService CreateService(IServiceScopeFactory scopeFactory, FakeEventBroadcaster broadcaster)
        => new(scopeFactory, new SessionProgressObserver(), broadcaster, NullLogger<SessionProgressService>.Instance);

    [Fact]
    public async Task A_todo_list_is_stored_and_pushed_to_the_row_and_the_open_session()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var scopeFactory = TestServiceScopeFactory.Create(services =>
            services.AddScoped<ISessionProgressRepository>(_ => new SessionProgressRepository(factory, new TestUserContext(OwnerId))));
        var broadcaster = new FakeEventBroadcaster();
        var service = CreateService(scopeFactory, broadcaster);

        var progress = await service.ApplyAsync(
            Todos(session.Id, OwnerId, ("Write the migration", TodoStatuses.Completed), ("Drop the indexes", TodoStatuses.InProgress)),
            CancellationToken.None);

        progress.ShouldNotBeNull();
        var stored = await new SessionProgressRepository(factory, new TestUserContext(OwnerId)).GetAsync(session.Id, CancellationToken.None);
        stored.ShouldNotBeNull().Done.ShouldBe(1);

        broadcaster.Broadcasts.Count.ShouldBe(2);
        var row = broadcaster.Broadcasts[0];
        (row.Topic, row.Type, row.UserId).ShouldBe(("sessions", "session_progress", OwnerId));
        JsonSerializer.Serialize(row.Payload).ShouldBe(
            $$"""{"sessionId":"{{session.Id}}","kind":"todos","done":1,"total":2,"current":"Drop the indexes"}""");

        var detail = broadcaster.Broadcasts[1];
        (detail.Topic, detail.Type, detail.UserId).ShouldBe(($"session:{session.Id}", "progress.updated", OwnerId));
        detail.Payload.GetProperty("todos").GetArrayLength().ShouldBe(2);
        detail.Payload.GetProperty("todos")[1].GetProperty("status").GetString().ShouldBe(TodoStatuses.InProgress);
        detail.Payload.GetProperty("updatedAt").GetString().ShouldBe("2026-09-13T12:24:00.0000000Z");
    }

    [Fact]
    public async Task The_same_list_again_is_not_pushed()
    {
        var repository = new InMemorySessionProgressRepository();
        var scopeFactory = TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionProgressRepository>(repository));
        var broadcaster = new FakeEventBroadcaster();
        var service = CreateService(scopeFactory, broadcaster);

        await service.ApplyAsync(Todos("fleet-1", OwnerId, ("One", TodoStatuses.InProgress)), CancellationToken.None);
        var again = await service.ApplyAsync(Todos("fleet-1", OwnerId, ("One", TodoStatuses.InProgress)), CancellationToken.None);

        again.ShouldBeNull();
        broadcaster.Broadcasts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Progress_for_someone_elses_session_is_neither_stored_nor_pushed()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var scopeFactory = TestServiceScopeFactory.Create(services =>
            services.AddScoped<ISessionProgressRepository>(_ => new SessionProgressRepository(factory, new TestUserContext("other-user"))));
        var broadcaster = new FakeEventBroadcaster();
        var service = CreateService(scopeFactory, broadcaster);

        var progress = await service.ApplyAsync(Todos(session.Id, "other-user", ("One", TodoStatuses.Pending)), CancellationToken.None);

        progress.ShouldBeNull();
        broadcaster.Broadcasts.ShouldBeEmpty();
        (await new SessionProgressRepository(factory, new TestUserContext(OwnerId)).GetAsync(session.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_service_applies_what_the_observer_queues()
    {
        var repository = new InMemorySessionProgressRepository();
        var scopeFactory = TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionProgressRepository>(repository));
        var broadcaster = new FakeEventBroadcaster();
        var observer = new SessionProgressObserver();
        var service = new SessionProgressService(scopeFactory, observer, broadcaster, NullLogger<SessionProgressService>.Instance);
        var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.OnBroadcast = (_, type, _, _, _) =>
        {
            if (type == "progress.updated")
                pushed.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        observer.Observe("fleet-1", OwnerId, Todos("fleet-1", OwnerId, ("One", TodoStatuses.Completed)).Event);

        await pushed.Task.WaitAsync(cts.Token);
        repository.All.ShouldHaveSingleItem().Done.ShouldBe(1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_observer_ignores_other_events_and_events_without_an_owner()
    {
        var observer = new SessionProgressObserver();
        var todos = Todos("fleet-1", OwnerId, ("One", TodoStatuses.Pending)).Event;

        observer.Observe("fleet-1", OwnerId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = "fleet-1" } });
        observer.Observe("fleet-1", OwnerId, null);
        observer.Observe("fleet-1", null, todos);
        observer.Observe("", OwnerId, todos);

        observer.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_todo_event_from_any_harness_reaches_the_observer_through_the_relay()
    {
        var sessionRepo = new InMemorySessionRepository();
        sessionRepo.Seed(new Session { Id = "fleet-1", InstanceId = "instance-1", UserId = "user-x", HarnessType = "test" });
        var activityTracker = new SessionActivityTracker();
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessionRepo);
            services.AddSingleton(new SessionCapabilitiesResolver(new InstanceTracker(), activityTracker));
            services.AddTransient<DomainEventTranslator>();
        });
        var tracker = new InstanceTracker();
        var observer = new SessionProgressObserver();
        var relay = new HarnessEventRelay(
            tracker, new FakeEventBroadcaster(), new FakeEventPublisher(), activityTracker, scopeFactory,
            NullLogger<HarnessEventRelay>.Instance, progressObserver: observer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        var instance = new FakeHarnessSession("instance-1") { HarnessType = "test" };
        tracker.Register("instance-1", instance);
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.TodosReported,
            SessionId = "harness-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { items = new[] { new { content = "One", status = "in_progress" } } }),
        });

        var observed = await observer.Reader.ReadAsync(cts.Token);
        (observed.SessionId, observed.UserId).ShouldBe(("fleet-1", "user-x"));
        observed.Event.ShouldBeOfType<TodosReported>().Payload.Items.ShouldHaveSingleItem().Content.ShouldBe("One");

        instance.Complete();
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }
}
