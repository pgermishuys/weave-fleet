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
        {
            services.AddScoped<ISessionProgressRepository>(_ => new SessionProgressRepository(factory, new TestUserContext(OwnerId)));
            services.AddScoped<ISessionRepository>(_ => new SessionRepository(factory, new TestUserContext(OwnerId)));
        });
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
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<ISessionProgressRepository>(repository);
            services.AddSingleton<ISessionRepository>(new InMemorySessionRepository());
        });
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
        {
            services.AddScoped<ISessionProgressRepository>(_ => new SessionProgressRepository(factory, new TestUserContext("other-user")));
            services.AddScoped<ISessionRepository>(_ => new SessionRepository(factory, new TestUserContext("other-user")));
        });
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
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<ISessionProgressRepository>(repository);
            services.AddSingleton<ISessionRepository>(new InMemorySessionRepository());
        });
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

        observer.Observe("fleet-1", OwnerId, new FilesChanged { Payload = new FilesChangedPayload { SessionId = "fleet-1" } });
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

    // ── Plans ────────────────────────────────────────────────────────────────

    private const string PlanText = """
        # Thin Proxy Simplification
        ### Phase 1: Proxy
        - [x] 1. Create the proxy
        - [ ] 2. Map the messages
        ### Phase 2: Switch
        - [ ] 3. Replace the snapshot
        """;

    private static ObservedProgressEvent Written(string sessionId, string userId, DateTimeOffset at, params string[] paths)
        => new(sessionId, userId, new FilesWritten { Payload = new FilesWrittenPayload { SessionId = sessionId, MessageId = "msg-1", Paths = paths } }, at);

    private static ObservedProgressEvent Idled(string sessionId, string userId, DateTimeOffset at)
        => new(sessionId, userId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = sessionId } }, at);

    private sealed record PlanFixture(
        Microsoft.Data.Sqlite.SqliteConnection Keeper,
        WeaveFleet.Application.Data.IDbConnectionFactory Factory,
        SessionProgressService Service,
        FakeEventBroadcaster Broadcaster,
        SessionProgressRepository Repository,
        string SessionId,
        string Directory) : IDisposable
    {
        public string PlanPath => Path.Combine(Directory, ".weave", "plans", "thin-proxy.md");

        public void Dispose()
        {
            Keeper.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<PlanFixture> CreatePlanFixtureAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"progress-plans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, ".weave", "plans"));
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: directory);
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddScoped<ISessionProgressRepository>(_ => new SessionProgressRepository(factory, new TestUserContext(OwnerId)));
            services.AddScoped<ISessionRepository>(_ => new SessionRepository(factory, new TestUserContext(OwnerId)));
        });
        var broadcaster = new FakeEventBroadcaster();
        return new PlanFixture(
            keeper,
            factory,
            CreateService(scopeFactory, broadcaster),
            broadcaster,
            new SessionProgressRepository(factory, new TestUserContext(OwnerId)),
            session.Id,
            directory);
    }

    [Fact]
    public async Task A_written_plan_file_becomes_the_sessions_progress()
    {
        using var fixture = await CreatePlanFixtureAsync();
        await File.WriteAllTextAsync(fixture.PlanPath, PlanText);

        var progress = await fixture.Service.ApplyAsync(
            Written(fixture.SessionId, OwnerId, DateTimeOffset.UtcNow, fixture.PlanPath, Path.Combine(fixture.Directory, "src", "Code.cs")),
            CancellationToken.None);

        progress.ShouldNotBeNull();
        (progress.Kind, progress.Done, progress.Total, progress.Current).ShouldBe(("plan", 1, 3, "Map the messages"));
        progress.Plans.ShouldHaveSingleItem().Path.ShouldBe(".weave/plans/thin-proxy.md");
        (await fixture.Repository.GetAsync(fixture.SessionId, CancellationToken.None)).ShouldNotBeNull().Plans.ShouldHaveSingleItem();

        JsonSerializer.Serialize(fixture.Broadcaster.Broadcasts[0].Payload).ShouldBe(
            $$"""{"sessionId":"{{fixture.SessionId}}","kind":"plan","done":1,"total":3,"current":"Map the messages"}""");
        var plan = fixture.Broadcaster.Broadcasts[1].Payload.GetProperty("plan");
        plan.GetProperty("path").GetString().ShouldBe(".weave/plans/thin-proxy.md");
        plan.GetProperty("groups").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_tick_made_outside_a_tool_call_is_picked_up_when_the_turn_ends()
    {
        using var fixture = await CreatePlanFixtureAsync();
        await File.WriteAllTextAsync(fixture.PlanPath, PlanText);
        await fixture.Service.ApplyAsync(Written(fixture.SessionId, OwnerId, DateTimeOffset.UtcNow, fixture.PlanPath), CancellationToken.None);

        // Ticked by a shell command, or in the user's editor: no files.written event.
        await File.WriteAllTextAsync(fixture.PlanPath, PlanText.Replace("- [ ] 2.", "- [x] 2.", StringComparison.Ordinal));
        var idledAt = DateTimeOffset.UtcNow;
        var progress = await fixture.Service.ApplyAsync(Idled(fixture.SessionId, OwnerId, idledAt), CancellationToken.None);

        progress.ShouldNotBeNull();
        progress.Done.ShouldBe(2);
        var step = progress.Plans[0].Steps.Single(s => s.Key == "2");
        (step.TickedAt, step.TickedInMessageId).ShouldBe((idledAt, (string?)null));
        fixture.Broadcaster.Broadcasts.Count.ShouldBe(4);

        // A second turn end with nothing new pushes nothing.
        (await fixture.Service.ApplyAsync(Idled(fixture.SessionId, OwnerId, DateTimeOffset.UtcNow), CancellationToken.None)).ShouldBeNull();
        fixture.Broadcaster.Broadcasts.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Files_outside_the_session_folder_are_ignored()
    {
        using var fixture = await CreatePlanFixtureAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(outside, PlanText);

        try
        {
            var progress = await fixture.Service.ApplyAsync(Written(fixture.SessionId, OwnerId, DateTimeOffset.UtcNow, outside), CancellationToken.None);

            progress.ShouldBeNull();
            fixture.Broadcaster.Broadcasts.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_subagent_shows_under_the_parents_step_with_its_own_todo_counts()
    {
        using var fixture = await CreatePlanFixtureAsync();
        await File.WriteAllTextAsync(fixture.PlanPath, PlanText);
        await fixture.Service.ApplyAsync(Written(fixture.SessionId, OwnerId, DateTimeOffset.UtcNow, fixture.PlanPath), CancellationToken.None);

        // The subagent's own session, linked to the parent.
        var (_, _, child) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(fixture.Factory, OwnerId, directory: fixture.Directory);
        using (var connection = fixture.Factory.CreateConnection())
        {
            await Dapper.SqlMapper.ExecuteAsync(connection,
                "UPDATE sessions SET parent_session_id = @Parent, title = @Title WHERE id = @Child",
                new { Parent = fixture.SessionId, Title = "Map the messages (@shuttle subagent)", Child = child.Id });
        }

        await fixture.Service.ApplyAsync(Delegation(fixture.SessionId, new DelegationCreated
        {
            Payload = new DelegationCreatedPayload
            {
                DelegationId = "del-1", ParentSessionId = fixture.SessionId, Title = "shuttle", Status = "pending", CreatedAt = "2026-09-13T12:00:00Z",
            },
        }), CancellationToken.None);
        await fixture.Service.ApplyAsync(Delegation(fixture.SessionId, new DelegationUpdated
        {
            Payload = new DelegationUpdatedPayload
            {
                DelegationId = "del-1", ParentSessionId = fixture.SessionId, ChildSessionId = child.Id, Title = "shuttle", Status = "running", CreatedAt = "2026-09-13T12:00:00Z",
            },
        }), CancellationToken.None);

        fixture.Broadcaster.Broadcasts.Clear();
        await fixture.Service.ApplyAsync(Todos(child.Id, OwnerId, ("Map text parts", TodoStatuses.Completed), ("Map tool parts", TodoStatuses.InProgress)), CancellationToken.None);

        // The child's own row and detail, then the parent's detail with the child's counts (the parent's row is unchanged).
        fixture.Broadcaster.Broadcasts.Select(b => (b.Topic, b.Type)).ShouldBe(
        [
            ("sessions", "session_progress"),
            ($"session:{child.Id}", "progress.updated"),
            ($"session:{fixture.SessionId}", "progress.updated"),
        ]);
        var subagent = fixture.Broadcaster.Broadcasts[2].Payload.GetProperty("subagents")[0];
        subagent.GetProperty("stepKey").GetString().ShouldBe("2");
        subagent.GetProperty("childSessionId").GetString().ShouldBe(child.Id);
        subagent.GetProperty("title").GetString().ShouldBe("Map the messages (@shuttle subagent)");
        (subagent.GetProperty("done").GetInt32(), subagent.GetProperty("total").GetInt32()).ShouldBe((1, 2));
        subagent.GetProperty("current").GetString().ShouldBe("Map tool parts");
    }

    private static ObservedProgressEvent Delegation(string sessionId, DomainEvent evt) => new(sessionId, OwnerId, evt, DateTimeOffset.UtcNow);

    [Fact]
    public void The_observer_skips_file_writes_with_no_markdown()
    {
        var observer = new SessionProgressObserver();

        observer.Observe("fleet-1", OwnerId, Written("fleet-1", OwnerId, DateTimeOffset.UtcNow, "/work/a.cs", "/work/b.json").Event);
        observer.Reader.TryRead(out _).ShouldBeFalse();

        observer.Observe("fleet-1", OwnerId, Written("fleet-1", OwnerId, DateTimeOffset.UtcNow, "/work/a.cs", "/work/PLAN.md").Event);
        observer.Reader.TryRead(out var queued).ShouldBeTrue();
        queued!.Event.ShouldBeOfType<FilesWritten>();
    }
}
