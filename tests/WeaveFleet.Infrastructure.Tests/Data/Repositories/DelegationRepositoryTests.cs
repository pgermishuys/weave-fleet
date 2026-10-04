using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DelegationRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DelegationRepository Repo, WeaveFleet.Application.Data.IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DelegationRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    [Fact]
    public async Task GetByIdAsync_DoesNotReturnOtherUsersDelegation()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new DelegationRepository(factory, new TestUserContext("owner-user"));
        var delegation = new WeaveFleet.Domain.Entities.Delegation
        {
            Id = Guid.NewGuid().ToString(),
            ParentSessionId = ownerGraph.Session.Id,
            Title = "owner",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        await ownerRepo.InsertAsync(delegation);

        var repo = new DelegationRepository(factory, new TestUserContext());
        var result = await repo.GetByIdAsync(delegation.Id);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_DoesNotUpdateOtherUsersDelegation()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new DelegationRepository(factory, new TestUserContext("owner-user"));
        var delegation = new WeaveFleet.Domain.Entities.Delegation
        {
            Id = Guid.NewGuid().ToString(),
            ParentSessionId = ownerGraph.Session.Id,
            Title = "owner",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        await ownerRepo.InsertAsync(delegation);

        var repo = new DelegationRepository(factory, new TestUserContext());
        await repo.UpdateStatusAsync(delegation.Id, "completed", DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));

        var reloaded = await ownerRepo.GetByIdAsync(delegation.Id);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe("pending");
    }

    // At startup no sub-agent from the last run can still be going. One left "running" kept its
    // parent's conversation showing working dots forever.
    [Fact]
    public async Task CancelAllUnfinishedAsync_CancelsPendingAndRunningDelegationsOfEveryUser()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var alice = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "alice");
        var bob = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "bob");
        var aliceRepo = new DelegationRepository(factory, new TestUserContext("alice"));
        var bobRepo = new DelegationRepository(factory, new TestUserContext("bob"));
        var pending = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "pending");
        var running = await InsertDelegationAsync(bobRepo, bob.Session.Id, "running");
        var completed = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "completed");

        var cancelled = await new DelegationRepository(factory, new TestUserContext())
            .CancelAllUnfinishedAsync("2026-09-15T08:00:00.0000000Z");

        cancelled.ShouldBe(2);
        (await aliceRepo.GetByIdAsync(pending.Id))!.Status.ShouldBe("cancelled");
        var runningAfter = await bobRepo.GetByIdAsync(running.Id);
        runningAfter!.Status.ShouldBe("cancelled");
        runningAfter.CompletedAt.ShouldBe("2026-09-15T08:00:00.0000000Z");
        (await aliceRepo.GetByIdAsync(completed.Id))!.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task Running_work_round_trips_with_what_the_harness_said_about_it()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;
        var alice = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "alice");
        var repo = new DelegationRepository(factory, new TestUserContext("alice"));
        var started = "2026-10-04T10:00:00.0000000Z";
        await repo.InsertAsync(new WeaveFleet.Domain.Entities.Delegation
        {
            Id = "w-1",
            ParentSessionId = alice.Session.Id,
            ParentToolCallId = "call_shell",
            Title = "shell",
            Status = "running",
            CreatedAt = started,
            UpdatedAt = started,
            Kind = WorkKinds.Shell,
            WorkId = "sh_1",
            Label = "bun run test:e2e",
            Background = true,
            CanStop = true,
            CanReadOutput = true,
        });

        var work = (await repo.GetByWorkIdAsync(alice.Session.Id, "sh_1")).ShouldNotBeNull();
        work.Kind.ShouldBe(WorkKinds.Shell);
        work.Label.ShouldBe("bun run test:e2e");
        work.Background.ShouldBeTrue();
        work.CanStop.ShouldBeTrue();
        work.CanReadOutput.ShouldBeTrue();
        (await repo.CountRunningAsync([alice.Session.Id, "other"])).ShouldBe(new Dictionary<string, int> { [alice.Session.Id] = 1 });

        work.Label = "bun run test:e2e --watch";
        work.CanReadOutput = false;
        work.Detail = "2 events";
        await repo.UpdateWorkAsync(work);
        var updated = (await repo.GetByIdAsync("w-1")).ShouldNotBeNull();
        updated.Label.ShouldBe("bun run test:e2e --watch");
        updated.CanReadOutput.ShouldBeFalse();
        updated.Detail.ShouldBe("2 events");

        await repo.EndAsync("w-1", "completed", WorkEndedReasons.Completed, "exit 0", "2026-10-04T10:05:00.0000000Z");
        var ended = (await repo.GetByIdAsync("w-1")).ShouldNotBeNull();
        ended.Status.ShouldBe("completed");
        ended.EndedReason.ShouldBe(WorkEndedReasons.Completed);
        ended.Detail.ShouldBe("exit 0");
        ended.CompletedAt.ShouldBe("2026-10-04T10:05:00.0000000Z");
        (await repo.ListRunningAsync()).ShouldBeEmpty();
        (await repo.CountRunningAsync([alice.Session.Id])).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_running_list_is_the_current_users_own()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;
        var alice = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "alice");
        var bob = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "bob");
        var aliceRepo = new DelegationRepository(factory, new TestUserContext("alice"));
        var bobRepo = new DelegationRepository(factory, new TestUserContext("bob"));
        var mine = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "running");
        await InsertDelegationAsync(bobRepo, bob.Session.Id, "running");

        (await aliceRepo.ListRunningAsync()).Select(d => d.Id).ShouldBe([mine.Id]);
        (await aliceRepo.CountRunningAsync([alice.Session.Id, bob.Session.Id])).Keys.ShouldBe([alice.Session.Id]);
        (await aliceRepo.GetByWorkIdAsync(bob.Session.Id, mine.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_subagent_that_finishes_ends_the_way_its_status_says_and_one_cancelled_at_startup_was_lost()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;
        var alice = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "alice");
        var repo = new DelegationRepository(factory, new TestUserContext("alice"));
        var finished = await InsertDelegationAsync(repo, alice.Session.Id, "running");
        var left = await InsertDelegationAsync(repo, alice.Session.Id, "running");

        await repo.UpdateStatusAsync(finished.Id, "error", DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));
        await repo.CancelAllUnfinishedAsync(DateTime.UtcNow.ToString("O"));

        (await repo.GetByIdAsync(finished.Id))!.EndedReason.ShouldBe(WorkEndedReasons.Error);
        var lost = (await repo.GetByIdAsync(left.Id))!;
        lost.Status.ShouldBe("cancelled");
        lost.EndedReason.ShouldBe(WorkEndedReasons.Lost);
        // A delegation inserted the old way is a subagent known by its own id when it has no call.
        lost.Kind.ShouldBe(WorkKinds.Subagent);
        lost.WorkId.ShouldBe(left.Id);
    }

    [Fact]
    public async Task Lost_work_is_listed_until_the_agent_was_told_and_only_for_its_owner()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;
        var alice = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "alice");
        await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "bob");
        var aliceRepo = new DelegationRepository(factory, new TestUserContext("alice"));
        var bobRepo = new DelegationRepository(factory, new TestUserContext("bob"));
        var lost = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "running");
        var finished = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "running");
        var running = await InsertDelegationAsync(aliceRepo, alice.Session.Id, "running");
        await aliceRepo.EndAsync(lost.Id, "cancelled", WorkEndedReasons.Lost, null, DateTime.UtcNow.ToString("O"));
        await aliceRepo.EndAsync(finished.Id, "completed", WorkEndedReasons.Completed, "exit 0", DateTime.UtcNow.ToString("O"));

        (await aliceRepo.GetUnreportedLostAsync(alice.Session.Id)).Select(d => d.Id).ShouldBe([lost.Id]);
        (await bobRepo.GetUnreportedLostAsync(alice.Session.Id)).ShouldBeEmpty();

        // Another user can't mark it; its owner can, once.
        await bobRepo.MarkLostReportedAsync([lost.Id], DateTime.UtcNow.ToString("O"));
        (await aliceRepo.GetUnreportedLostAsync(alice.Session.Id)).ShouldHaveSingleItem();
        await aliceRepo.MarkLostReportedAsync([lost.Id], DateTime.UtcNow.ToString("O"));
        (await aliceRepo.GetUnreportedLostAsync(alice.Session.Id)).ShouldBeEmpty();
        (await aliceRepo.GetByIdAsync(running.Id))!.IsRunning.ShouldBeTrue();
    }

    private static async Task<WeaveFleet.Domain.Entities.Delegation> InsertDelegationAsync(
        DelegationRepository repo,
        string parentSessionId,
        string status)
    {
        var delegation = new WeaveFleet.Domain.Entities.Delegation
        {
            Id = Guid.NewGuid().ToString(),
            ParentSessionId = parentSessionId,
            Title = status,
            Status = status,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        await repo.InsertAsync(delegation);
        return delegation;
    }
}
