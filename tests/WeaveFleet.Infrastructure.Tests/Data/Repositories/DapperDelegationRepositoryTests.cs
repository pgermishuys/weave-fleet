using Microsoft.Data.Sqlite;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperDelegationRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperDelegationRepository Repo, WeaveFleet.Application.Data.IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperDelegationRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    [Fact]
    public async Task GetByIdAsync_DoesNotReturnOtherUsersDelegation()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new DapperDelegationRepository(factory, new TestUserContext("owner-user"));
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

        var repo = new DapperDelegationRepository(factory, new TestUserContext());
        var result = await repo.GetByIdAsync(delegation.Id);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_DoesNotUpdateOtherUsersDelegation()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new DapperDelegationRepository(factory, new TestUserContext("owner-user"));
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

        var repo = new DapperDelegationRepository(factory, new TestUserContext());
        await repo.UpdateStatusAsync(delegation.Id, "completed", DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));

        var reloaded = await ownerRepo.GetByIdAsync(delegation.Id);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe("pending");
    }
}
