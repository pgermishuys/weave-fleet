using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class SessionCallbackRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, SessionCallbackRepository Repo, WeaveFleet.Application.Data.IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new SessionCallbackRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    [Fact]
    public async Task GetPendingForSessionAsync_DoesNotReturnOtherUsersCallbacks()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerTarget = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user", directory: "/tmp/target");
        var ownerRepo = new SessionCallbackRepository(factory, new TestUserContext("owner-user"));
        await ownerRepo.InsertAsync(new SessionCallback
        {
            Id = Guid.NewGuid().ToString(),
            SourceSessionId = ownerGraph.Session.Id,
            TargetSessionId = ownerTarget.Session.Id,
            TargetInstanceId = ownerTarget.Instance.Id,
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var repo = new SessionCallbackRepository(factory, new TestUserContext());
        var callbacks = await repo.GetPendingForSessionAsync(ownerGraph.Session.Id);

        callbacks.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimPendingAsync_ReturnsFalseForOtherUsersCallback()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerTarget = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user", directory: "/tmp/target");
        var ownerRepo = new SessionCallbackRepository(factory, new TestUserContext("owner-user"));
        var callback = new SessionCallback
        {
            Id = Guid.NewGuid().ToString(),
            SourceSessionId = ownerGraph.Session.Id,
            TargetSessionId = ownerTarget.Session.Id,
            TargetInstanceId = ownerTarget.Instance.Id,
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        await ownerRepo.InsertAsync(callback);

        var repo = new SessionCallbackRepository(factory, new TestUserContext());
        var claimed = await repo.ClaimPendingAsync(callback.Id);

        claimed.ShouldBeFalse();
    }
}
