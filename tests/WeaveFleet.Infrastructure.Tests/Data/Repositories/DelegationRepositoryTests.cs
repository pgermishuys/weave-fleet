using Microsoft.Data.Sqlite;
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
