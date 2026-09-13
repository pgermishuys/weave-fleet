using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class SessionProgressRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 24, 0, TimeSpan.Zero);

    private static async Task<(SqliteConnection Keeper, IDbConnectionFactory Factory, SessionProgressRepository Repo, Session Session)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        return (keeper, factory, new SessionProgressRepository(factory, new TestUserContext(OwnerId)), session);
    }

    private static SessionProgress Progress(string sessionId, int done = 1, string userId = OwnerId) => new()
    {
        SessionId = sessionId,
        UserId = userId,
        Kind = SessionProgressKinds.Todos,
        Done = done,
        Total = 3,
        Current = "Drop the associated indexes",
        Todos =
        [
            new TodoEntry { Content = "Write DROP TABLE statements", Status = TodoStatuses.Completed, Priority = "high" },
            new TodoEntry { Content = "Drop the associated indexes", Status = TodoStatuses.InProgress },
            new TodoEntry { Content = "Start the app", Status = TodoStatuses.Pending, Priority = "low" },
        ],
        UpdatedAt = Now,
    };

    [Fact]
    public async Task UpsertAsync_RoundTripsProgress_WithItsTodos()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;

        (await repo.UpsertAsync(Progress(session.Id), CancellationToken.None)).ShouldBeTrue();

        var stored = (await repo.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull();
        (stored.Kind, stored.Done, stored.Total, stored.Current).ShouldBe((SessionProgressKinds.Todos, 1, 3, "Drop the associated indexes"));
        stored.Todos.ShouldBe(Progress(session.Id).Todos);
        stored.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task UpsertAsync_RoundTripsPlansWithTheirTicks()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var plan = new TrackedPlan
        {
            Path = ".weave/plans/thin-proxy.md",
            Title = "Thin Proxy Simplification",
            TrackedSince = Now,
            LastWrittenAt = Now.AddMinutes(9),
            LastTickedAt = Now.AddMinutes(9),
            Groups =
            [
                new TrackedPlanGroup
                {
                    Title = "Phase 4: Delete dead code and tables",
                    Steps =
                    [
                        new TrackedPlanStep { Key = "10", Number = "10", Title = "Remove repository classes", Checked = true },
                        new TrackedPlanStep
                        {
                            Key = "11", Number = "11", Title = "Add migration to drop dead tables", Checked = true,
                            SubDone = 1, SubTotal = 2, TickedAt = Now.AddMinutes(9), TickedInMessageId = "msg-9",
                        },
                        new TrackedPlanStep { Key = "12", Number = "12", Title = "Remove `ApplyStreamingDeltas`" },
                    ],
                },
            ],
        };

        await repo.UpsertAsync(Progress(session.Id) with { Kind = SessionProgressKinds.Plan, Plans = [plan] }, CancellationToken.None);

        var stored = (await repo.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull();
        var storedPlan = stored.Plans.ShouldHaveSingleItem();
        (storedPlan.Path, storedPlan.Title, storedPlan.TrackedSince, storedPlan.LastTickedAt).ShouldBe((plan.Path, plan.Title, Now, Now.AddMinutes(9)));
        storedPlan.Groups.ShouldHaveSingleItem().Steps.ShouldBe(plan.Groups[0].Steps);
    }

    [Fact]
    public async Task UpsertAsync_ReplacesExistingProgress()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        await repo.UpsertAsync(Progress(session.Id, done: 1), CancellationToken.None);

        (await repo.UpsertAsync(Progress(session.Id, done: 3) with { Current = null, Todos = [] }, CancellationToken.None)).ShouldBeTrue();

        var stored = (await repo.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull();
        (stored.Done, stored.Current).ShouldBe((3, (string?)null));
        stored.Todos.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ReturnsFalse_ForUnknownSession()
    {
        var (keeper, _, repo, _) = await CreateAsync();
        using var _ = keeper;

        (await repo.UpsertAsync(Progress("no-such-session"), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadsAndWrites_AreScopedToTheOwner()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        await repo.UpsertAsync(Progress(session.Id), CancellationToken.None);
        var otherRepo = new SessionProgressRepository(factory, new TestUserContext("other-user"));

        (await otherRepo.GetAsync(session.Id, CancellationToken.None)).ShouldBeNull();
        (await otherRepo.GetManyAsync([session.Id], CancellationToken.None)).ShouldBeEmpty();
        (await otherRepo.GetForOwnerAsync(session.Id, "other-user", CancellationToken.None)).ShouldBeNull();
        (await otherRepo.UpsertAsync(Progress(session.Id, done: 3, userId: "other-user"), CancellationToken.None)).ShouldBeFalse();

        (await repo.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull().Done.ShouldBe(1);
    }

    [Fact]
    public async Task GetForOwnerAsync_ReadsWithoutTheCurrentUser()
    {
        var (keeper, factory, _, session) = await CreateAsync();
        using var _ = keeper;
        await new SessionProgressRepository(factory, new TestUserContext(OwnerId)).UpsertAsync(Progress(session.Id), CancellationToken.None);
        var backgroundRepo = new SessionProgressRepository(factory, new TestUserContext("someone-else"));

        (await backgroundRepo.GetForOwnerAsync(session.Id, OwnerId, CancellationToken.None)).ShouldNotBeNull().Done.ShouldBe(1);
    }

    [Fact]
    public async Task GetManyAsync_ReturnsOnlySessionsWithProgress()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, otherSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/other");
        await repo.UpsertAsync(Progress(session.Id), CancellationToken.None);

        var found = await repo.GetManyAsync([session.Id, otherSession.Id, "no-such-session"], CancellationToken.None);

        found.Keys.ShouldBe([session.Id]);
        (await repo.GetManyAsync([], CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingASession_DeletesItsProgress()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        await repo.UpsertAsync(Progress(session.Id), CancellationToken.None);

        var sessionRepo = new SessionRepository(factory, new TestUserContext(OwnerId));
        (await sessionRepo.DeleteAsync(session.Id)).ShouldBeTrue();

        (await repo.GetAsync(session.Id, CancellationToken.None)).ShouldBeNull();
    }
}
