using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class CanvasRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static async Task<(SqliteConnection Keeper, IDbConnectionFactory Factory, CanvasRepository Repo, Session Session)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        return (keeper, factory, new CanvasRepository(factory, new TestUserContext(OwnerId)), session);
    }

    private static (Canvas Canvas, CanvasRevision Revision) NewCanvas(
        string sessionId,
        string? id = null,
        string title = "Session event flow",
        string createdAt = "2026-09-12T10:00:00.0000000Z")
    {
        var canvas = new Canvas
        {
            Id = id ?? $"cv_{Guid.NewGuid():N}",
            SessionId = sessionId,
            Kind = "diagram",
            Title = title,
            StateJson = """{"direction":"TB","nodes":[{"id":"n1","label":"NuCode session"}],"edges":[]}""",
            Version = 1,
            AgentSeenVersion = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var revision = new CanvasRevision
        {
            CanvasId = canvas.Id,
            Version = 1,
            Actor = CanvasRevision.AgentActor,
            OpsJson = """[{"op":"open"}]""",
            CreatedAt = createdAt,
        };
        return (canvas, revision);
    }

    private static (Canvas Canvas, CanvasRevision Revision) NextVersion(
        Canvas current,
        string stateJson,
        string actor = CanvasRevision.UserActor,
        int? agentSeenVersion = null)
    {
        var version = current.Version + 1;
        var updatedAt = DateTime.UtcNow.ToString("O");
        var canvas = new Canvas
        {
            Id = current.Id,
            SessionId = current.SessionId,
            UserId = current.UserId,
            Kind = current.Kind,
            Title = current.Title,
            StateJson = stateJson,
            Version = version,
            AgentSeenVersion = agentSeenVersion ?? current.AgentSeenVersion,
            CreatedAt = current.CreatedAt,
            UpdatedAt = updatedAt,
            ClosedAt = current.ClosedAt,
        };
        var revision = new CanvasRevision
        {
            CanvasId = current.Id,
            Version = version,
            Actor = actor,
            OpsJson = $$"""[{"op":"moveNode","id":"n1","x":{{version}},"y":0}]""",
            CreatedAt = updatedAt,
        };
        return (canvas, revision);
    }

    [Fact]
    public async Task InsertAsync_RoundTripsCanvasAndFirstRevision()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);

        (await repo.InsertAsync(canvas, revision)).ShouldBeTrue();

        var stored = await repo.GetByIdAsync(session.Id, canvas.Id);
        stored.ShouldNotBeNull();
        stored.ShouldSatisfyAllConditions(
            c => c.SessionId.ShouldBe(session.Id),
            c => c.UserId.ShouldBe(OwnerId),
            c => c.Kind.ShouldBe("diagram"),
            c => c.Title.ShouldBe("Session event flow"),
            c => c.StateJson.ShouldBe(canvas.StateJson),
            c => c.Version.ShouldBe(1),
            c => c.AgentSeenVersion.ShouldBe(1),
            c => c.CreatedAt.ShouldBe(canvas.CreatedAt),
            c => c.UpdatedAt.ShouldBe(canvas.UpdatedAt),
            c => c.ClosedAt.ShouldBeNull());

        var revisions = await repo.ListRevisionsAsync(canvas.Id, afterVersion: 0);
        revisions.Count.ShouldBe(1);
        revisions[0].ShouldSatisfyAllConditions(
            r => r.CanvasId.ShouldBe(canvas.Id),
            r => r.Version.ShouldBe(1),
            r => r.Actor.ShouldBe(CanvasRevision.AgentActor),
            r => r.OpsJson.ShouldBe(revision.OpsJson),
            r => r.CreatedAt.ShouldBe(revision.CreatedAt));
    }

    [Fact]
    public async Task InsertAsync_ReturnsFalseAndWritesNothing_ForAnotherUsersSession()
    {
        var (keeper, factory, _, session) = await CreateAsync();
        using var _ = keeper;
        var otherRepo = new CanvasRepository(factory, new TestUserContext("other-user"));
        var (canvas, revision) = NewCanvas(session.Id);

        (await otherRepo.InsertAsync(canvas, revision)).ShouldBeFalse();

        using var conn = factory.CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM canvases")).ShouldBe(0);
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM canvas_revisions")).ShouldBe(0);
    }

    [Fact]
    public async Task InsertAsync_ReturnsFalse_ForUnknownSession()
    {
        var (keeper, _, repo, _) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas("no-such-session");

        (await repo.InsertAsync(canvas, revision)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadsAndWrites_AreScopedToTheOwner()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var otherRepo = new CanvasRepository(factory, new TestUserContext("other-user"));

        (await otherRepo.GetByIdAsync(session.Id, canvas.Id)).ShouldBeNull();
        (await otherRepo.GetByTitleAsync(session.Id, canvas.Title)).ShouldBeNull();
        (await otherRepo.ListBySessionIdAsync(session.Id, includeClosed: true)).ShouldBeEmpty();
        (await otherRepo.ListRevisionsAsync(canvas.Id, afterVersion: 0)).ShouldBeEmpty();
        (await otherRepo.SetClosedAtAsync(session.Id, canvas.Id, "2026-09-12T11:00:00Z")).ShouldBeFalse();
        var (next, nextRevision) = NextVersion(canvas, """{"nodes":[],"edges":[]}""");
        (await otherRepo.TryUpdateAsync(next, expectedVersion: 1, nextRevision)).ShouldBeFalse();
        await otherRepo.MarkAgentSeenAsync(session.Id, canvas.Id, 1);

        var stored = await repo.GetByIdAsync(session.Id, canvas.Id);
        stored!.Version.ShouldBe(1);
        stored.StateJson.ShouldBe(canvas.StateJson);
        stored.ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_ForCanvasOfAnotherSession()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, otherSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/other");
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);

        (await repo.GetByIdAsync(otherSession.Id, canvas.Id)).ShouldBeNull();
        (await repo.SetClosedAtAsync(otherSession.Id, canvas.Id, "2026-09-12T11:00:00Z")).ShouldBeFalse();
    }

    [Fact]
    public async Task ListBySessionIdAsync_ReturnsOpenCanvasesOldestFirst_AndClosedOnlyWhenAsked()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (second, secondRevision) = NewCanvas(session.Id, title: "Second", createdAt: "2026-09-12T10:05:00.0000000Z");
        var (first, firstRevision) = NewCanvas(session.Id, title: "First", createdAt: "2026-09-12T10:00:00.0000000Z");
        var (closed, closedRevision) = NewCanvas(session.Id, title: "Closed", createdAt: "2026-09-12T10:10:00.0000000Z");
        await repo.InsertAsync(second, secondRevision);
        await repo.InsertAsync(first, firstRevision);
        await repo.InsertAsync(closed, closedRevision);
        (await repo.SetClosedAtAsync(session.Id, closed.Id, "2026-09-12T10:20:00Z")).ShouldBeTrue();

        (await repo.ListBySessionIdAsync(session.Id)).Select(c => c.Title).ShouldBe(["First", "Second"]);
        (await repo.ListBySessionIdAsync(session.Id, includeClosed: true)).Select(c => c.Title).ShouldBe(["First", "Second", "Closed"]);
    }

    [Fact]
    public async Task SetClosedAtAsync_ClosesAndReopens()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);

        (await repo.SetClosedAtAsync(session.Id, canvas.Id, "2026-09-12T11:00:00Z")).ShouldBeTrue();
        (await repo.GetByIdAsync(session.Id, canvas.Id))!.ClosedAt.ShouldBe("2026-09-12T11:00:00Z");

        (await repo.SetClosedAtAsync(session.Id, canvas.Id, null)).ShouldBeTrue();
        (await repo.GetByIdAsync(session.Id, canvas.Id))!.ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task GetByTitleAsync_FindsClosedCanvases_AndPrefersTheMostRecentlyUpdated()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (older, olderRevision) = NewCanvas(session.Id, title: "Flow", createdAt: "2026-09-12T10:00:00.0000000Z");
        var (newer, newerRevision) = NewCanvas(session.Id, title: "Flow", createdAt: "2026-09-12T10:30:00.0000000Z");
        await repo.InsertAsync(older, olderRevision);
        await repo.InsertAsync(newer, newerRevision);
        await repo.SetClosedAtAsync(session.Id, newer.Id, "2026-09-12T11:00:00Z");

        var found = await repo.GetByTitleAsync(session.Id, "Flow");

        found.ShouldNotBeNull();
        found.Id.ShouldBe(newer.Id);
        found.ClosedAt.ShouldNotBeNull();
        (await repo.GetByTitleAsync(session.Id, "flow")).ShouldBeNull();
    }

    [Fact]
    public async Task TryUpdateAsync_WritesStateAndRevision_WhenVersionMatches()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var (next, nextRevision) = NextVersion(canvas, """{"direction":"LR","nodes":[],"edges":[]}""");

        (await repo.TryUpdateAsync(next, expectedVersion: 1, nextRevision)).ShouldBeTrue();

        var stored = await repo.GetByIdAsync(session.Id, canvas.Id);
        stored!.Version.ShouldBe(2);
        stored.StateJson.ShouldBe(next.StateJson);
        stored.UpdatedAt.ShouldBe(next.UpdatedAt);
        stored.CreatedAt.ShouldBe(canvas.CreatedAt);

        var revisions = await repo.ListRevisionsAsync(canvas.Id, afterVersion: 1);
        revisions.Count.ShouldBe(1);
        revisions[0].Version.ShouldBe(2);
        revisions[0].Actor.ShouldBe(CanvasRevision.UserActor);
        revisions[0].OpsJson.ShouldBe(nextRevision.OpsJson);
    }

    [Fact]
    public async Task TryUpdateAsync_ReturnsFalseAndWritesNothing_WhenVersionIsStale()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var (winner, winnerRevision) = NextVersion(canvas, """{"winner":true}""");
        var (loser, loserRevision) = NextVersion(canvas, """{"loser":true}""");

        (await repo.TryUpdateAsync(winner, expectedVersion: 1, winnerRevision)).ShouldBeTrue();
        (await repo.TryUpdateAsync(loser, expectedVersion: 1, loserRevision)).ShouldBeFalse();

        var stored = await repo.GetByIdAsync(session.Id, canvas.Id);
        stored!.Version.ShouldBe(2);
        stored.StateJson.ShouldBe("""{"winner":true}""");
        var revisions = await repo.ListRevisionsAsync(canvas.Id, afterVersion: 0);
        revisions.Select(r => r.Version).ShouldBe([1, 2]);
        revisions[1].OpsJson.ShouldBe(winnerRevision.OpsJson);
    }

    [Fact]
    public async Task TryUpdateAsync_ConcurrentWriters_OneWinsAndTheLoserRetriesAgainstTheNewState()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"weave-canvas-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new FleetOptions { DatabasePath = dbPath });
            using (var migrationConnection = factory.CreateConnection())
                await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).ApplyMigrationsAsync(migrationConnection);
            var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
            var repo = new CanvasRepository(factory, new TestUserContext(OwnerId));
            var (canvas, revision) = NewCanvas(session.Id);
            await repo.InsertAsync(canvas, revision);

            using var start = new Barrier(2);
            async Task<bool> WriteAsync(string stateJson)
            {
                var current = (await repo.GetByIdAsync(session.Id, canvas.Id))!;
                var (next, nextRevision) = NextVersion(current, stateJson);
                start.SignalAndWait();
                return await repo.TryUpdateAsync(next, current.Version, nextRevision);
            }

            var results = await Task.WhenAll(
                Task.Run(() => WriteAsync("""{"writer":"a"}""")),
                Task.Run(() => WriteAsync("""{"writer":"b"}""")));

            results.Count(won => won).ShouldBe(1);
            var afterRace = (await repo.GetByIdAsync(session.Id, canvas.Id))!;
            afterRace.Version.ShouldBe(2);

            var loserState = results[0] ? """{"writer":"b"}""" : """{"writer":"a"}""";
            var (retry, retryRevision) = NextVersion(afterRace, loserState);
            (await repo.TryUpdateAsync(retry, afterRace.Version, retryRevision)).ShouldBeTrue();

            var final = (await repo.GetByIdAsync(session.Id, canvas.Id))!;
            final.Version.ShouldBe(3);
            final.StateJson.ShouldBe(loserState);
            (await repo.ListRevisionsAsync(canvas.Id, afterVersion: 0)).Select(r => r.Version).ShouldBe([1, 2, 3]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TryUpdateAsync_NeverMovesAgentSeenVersionBackwards()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var (agentWrite, agentRevision) = NextVersion(canvas, "{}", CanvasRevision.AgentActor, agentSeenVersion: 2);
        await repo.TryUpdateAsync(agentWrite, expectedVersion: 1, agentRevision);

        // A user write built from a copy loaded before the agent's write still carries the old value.
        var (userWrite, userRevision) = NextVersion(agentWrite, "{}", agentSeenVersion: 1);
        (await repo.TryUpdateAsync(userWrite, expectedVersion: 2, userRevision)).ShouldBeTrue();

        var stored = await repo.GetByIdAsync(session.Id, canvas.Id);
        stored!.Version.ShouldBe(3);
        stored.AgentSeenVersion.ShouldBe(2);
    }

    [Fact]
    public async Task TryUpdateAsync_Throws_WhenVersionsDoNotLineUp()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var (next, nextRevision) = NextVersion(canvas, "{}");

        await Should.ThrowAsync<ArgumentException>(() => repo.TryUpdateAsync(next, expectedVersion: 2, nextRevision));

        nextRevision.Version = 3;
        await Should.ThrowAsync<ArgumentException>(() => repo.TryUpdateAsync(next, expectedVersion: 1, nextRevision));
    }

    [Fact]
    public async Task MarkAgentSeenAsync_MovesForwardOnly_AndStopsAtTheCurrentVersion()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        canvas.AgentSeenVersion = 0;
        await repo.InsertAsync(canvas, revision);
        var (v2, v2Revision) = NextVersion(canvas, "{}");
        await repo.TryUpdateAsync(v2, expectedVersion: 1, v2Revision);
        var (v3, v3Revision) = NextVersion(v2, "{}");
        await repo.TryUpdateAsync(v3, expectedVersion: 2, v3Revision);

        await repo.MarkAgentSeenAsync(session.Id, canvas.Id, 2);
        (await repo.GetByIdAsync(session.Id, canvas.Id))!.AgentSeenVersion.ShouldBe(2);

        await repo.MarkAgentSeenAsync(session.Id, canvas.Id, 1);
        (await repo.GetByIdAsync(session.Id, canvas.Id))!.AgentSeenVersion.ShouldBe(2);

        await repo.MarkAgentSeenAsync(session.Id, canvas.Id, 99);
        (await repo.GetByIdAsync(session.Id, canvas.Id))!.AgentSeenVersion.ShouldBe(3);
    }

    [Fact]
    public async Task ListRevisionsAsync_ReturnsOnlyRevisionsAfterTheGivenVersion()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (canvas, revision) = NewCanvas(session.Id);
        await repo.InsertAsync(canvas, revision);
        var (v2, v2Revision) = NextVersion(canvas, "{}", CanvasRevision.UserActor);
        await repo.TryUpdateAsync(v2, expectedVersion: 1, v2Revision);
        var (v3, v3Revision) = NextVersion(v2, "{}", CanvasRevision.AgentActor);
        await repo.TryUpdateAsync(v3, expectedVersion: 2, v3Revision);

        var revisions = await repo.ListRevisionsAsync(canvas.Id, afterVersion: 1);

        revisions.Select(r => (r.Version, r.Actor)).ShouldBe([(2, CanvasRevision.UserActor), (3, CanvasRevision.AgentActor)]);
        (await repo.ListRevisionsAsync(canvas.Id, afterVersion: 3)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingASession_DeletesItsCanvasesAndRevisions()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, otherSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/other");
        var (canvas, revision) = NewCanvas(session.Id);
        var (kept, keptRevision) = NewCanvas(otherSession.Id);
        await repo.InsertAsync(canvas, revision);
        await repo.InsertAsync(kept, keptRevision);
        var (v2, v2Revision) = NextVersion(canvas, "{}");
        await repo.TryUpdateAsync(v2, expectedVersion: 1, v2Revision);

        var sessionRepo = new SessionRepository(factory, new TestUserContext(OwnerId));
        (await sessionRepo.DeleteAsync(session.Id)).ShouldBeTrue();

        using var conn = factory.CreateConnection();
        (await conn.QueryAsync<string>("SELECT id FROM canvases")).ShouldBe([kept.Id]);
        (await conn.QueryAsync<string>("SELECT canvas_id FROM canvas_revisions")).ShouldBe([kept.Id]);
    }
}
