using Dapper;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class AppRunRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static async Task<(SqliteConnection Keeper, IDbConnectionFactory Factory, AppRunRepository Repo, Session Session)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        return (keeper, factory, new AppRunRepository(factory, new TestUserContext(OwnerId)), session);
    }

    private static AppRun NewRun(string sessionId, string id = "app_1", string status = "running", string createdAt = "2026-09-13T08:00:00.0000000Z") => new()
    {
        Id = id,
        SessionId = sessionId,
        Command = "bun run dev",
        Directory = "/work/shop",
        Port = 41234,
        Status = status,
        Url = "http://localhost:41234/",
        Pid = 4242,
        PidStartedAt = "2026-09-13T08:00:00.0000000+00:00",
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    [Fact]
    public async Task UpsertAsync_RoundTripsARun_AsTheCurrentUsers()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;

        (await repo.UpsertAsync(NewRun(session.Id))).ShouldBeTrue();

        var stored = (await repo.GetByIdAsync(session.Id, "app_1")).ShouldNotBeNull();
        stored.UserId.ShouldBe(OwnerId);
        (stored.Command, stored.Directory, stored.Port, stored.Status, stored.Url).ShouldBe(("bun run dev", "/work/shop", 41234, "running", "http://localhost:41234/"));
        (stored.Pid, stored.PidStartedAt, stored.ExitCode).ShouldBe((4242, "2026-09-13T08:00:00.0000000+00:00", (int?)null));
    }

    [Fact]
    public async Task UpsertAsync_UpdatesARun_ButKeepsWhenItWasCreated()
    {
        var (keeper, _, repo, session) = await CreateAsync();
        using var _ = keeper;
        await repo.UpsertAsync(NewRun(session.Id));

        var exited = NewRun(session.Id, status: "exited", createdAt: "2026-09-13T09:00:00.0000000Z");
        exited.ExitCode = 1;
        exited.Pid = null;
        exited.PidStartedAt = null;
        (await repo.UpsertAsync(exited)).ShouldBeTrue();

        var stored = (await repo.GetByIdAsync(session.Id, "app_1")).ShouldNotBeNull();
        (stored.Status, stored.ExitCode, stored.Pid).ShouldBe(("exited", 1, (int?)null));
        stored.CreatedAt.ShouldBe("2026-09-13T08:00:00.0000000Z");
        stored.UpdatedAt.ShouldBe("2026-09-13T09:00:00.0000000Z");
    }

    [Fact]
    public async Task ReadsAndWrites_AreScopedToTheOwner()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        await repo.UpsertAsync(NewRun(session.Id));
        var otherRepo = new AppRunRepository(factory, new TestUserContext("other-user"));

        (await otherRepo.GetByIdAsync(session.Id, "app_1")).ShouldBeNull();
        (await otherRepo.ListBySessionIdAsync(session.Id)).ShouldBeEmpty();
        (await otherRepo.UpsertAsync(NewRun(session.Id, status: "stopped"))).ShouldBeFalse();
        (await otherRepo.UpsertAsync(NewRun(session.Id, id: "app_2"))).ShouldBeFalse();

        (await repo.ListBySessionIdAsync(session.Id)).ShouldHaveSingleItem().Status.ShouldBe("running");
    }

    [Fact]
    public async Task UpsertAsync_ReturnsFalse_ForUnknownSession()
    {
        var (keeper, _, repo, _) = await CreateAsync();
        using var _ = keeper;

        (await repo.UpsertAsync(NewRun("no-such-session"))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_cannot_be_moved_to_another_session()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, otherSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/other");
        await repo.UpsertAsync(NewRun(session.Id));

        (await repo.UpsertAsync(NewRun(otherSession.Id, status: "stopped"))).ShouldBeFalse();

        (await repo.GetByIdAsync(session.Id, "app_1")).ShouldNotBeNull().Status.ShouldBe("running");
        (await repo.GetByIdAsync(otherSession.Id, "app_1")).ShouldBeNull();
    }

    [Fact]
    public async Task Startup_lists_every_users_unfinished_runs_and_marks_them_stopped()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, theirSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "other-user", directory: "/tmp/theirs");
        var theirRepo = new AppRunRepository(factory, new TestUserContext("other-user"));
        await repo.UpsertAsync(NewRun(session.Id, id: "app_running"));
        await repo.UpsertAsync(NewRun(session.Id, id: "app_exited", status: "exited"));
        await theirRepo.UpsertAsync(NewRun(theirSession.Id, id: "app_theirs", status: "starting"));

        var unfinished = await repo.ListUnfinishedForAllUsersAsync();
        unfinished.Select(run => run.Id).Order().ShouldBe(["app_running", "app_theirs"]);

        await repo.MarkStoppedForAllUsersAsync([.. unfinished.Select(run => run.Id)], "2026-09-13T10:00:00Z");

        (await repo.ListUnfinishedForAllUsersAsync()).ShouldBeEmpty();
        var theirs = (await theirRepo.GetByIdAsync(theirSession.Id, "app_theirs")).ShouldNotBeNull();
        (theirs.Status, theirs.Pid, theirs.PidStartedAt, theirs.UpdatedAt).ShouldBe(("stopped", (int?)null, (string?)null, "2026-09-13T10:00:00Z"));
        (await repo.GetByIdAsync(session.Id, "app_exited")).ShouldNotBeNull().Status.ShouldBe("exited");
    }

    [Fact]
    public async Task DeletingASession_DeletesItsRuns()
    {
        var (keeper, factory, repo, session) = await CreateAsync();
        using var _ = keeper;
        var (_, _, otherSession) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/other");
        await repo.UpsertAsync(NewRun(session.Id, id: "app_gone"));
        await repo.UpsertAsync(NewRun(otherSession.Id, id: "app_kept"));

        var sessionRepo = new SessionRepository(factory, new TestUserContext(OwnerId));
        (await sessionRepo.DeleteAsync(session.Id)).ShouldBeTrue();

        using var conn = factory.CreateConnection();
        (await conn.QueryAsync<string>("SELECT id FROM app_runs")).ShouldBe(["app_kept"]);
    }

    [Fact]
    public async Task PreviewCommand_IsSharedByTheWorktreesOfAProject_AndScopedToTheOwner()
    {
        var (keeper, factory, repo, inPlace) = await CreateAsync();
        using var _ = keeper;
        var (workspaceA, _, worktreeA) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/wt-a");
        var (workspaceB, _, worktreeB) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId, directory: "/tmp/wt-b");
        using (var conn = factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE workspaces SET source_directory = @Source, isolation_strategy = 'worktree' WHERE id IN (@A, @B)",
                new { Source = inPlace.Directory, A = workspaceA.Id, B = workspaceB.Id });
        }

        (await repo.GetPreviewCommandAsync(worktreeB.Id)).ShouldBeNull();

        await repo.RememberPreviewCommandAsync(worktreeA.Id, "bun run dev", "2026-09-13T08:00:00.0000000Z");
        (await repo.GetPreviewCommandAsync(worktreeB.Id)).ShouldBe("bun run dev");
        (await repo.GetPreviewCommandAsync(inPlace.Id)).ShouldBe("bun run dev");

        await repo.RememberPreviewCommandAsync(inPlace.Id, "npm run dev", "2026-09-13T09:00:00.0000000Z");
        (await repo.GetPreviewCommandAsync(worktreeA.Id)).ShouldBe("npm run dev");

        var otherRepo = new AppRunRepository(factory, new TestUserContext("other-user"));
        (await otherRepo.GetPreviewCommandAsync(inPlace.Id)).ShouldBeNull();
        await otherRepo.RememberPreviewCommandAsync(inPlace.Id, "make serve", "2026-09-13T10:00:00.0000000Z");
        (await repo.GetPreviewCommandAsync(inPlace.Id)).ShouldBe("npm run dev");
    }
}
