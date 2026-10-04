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

    private static async Task<SessionCallback> InsertOwnersCallbackAsync(WeaveFleet.Application.Data.IDbConnectionFactory factory, string status = "pending")
    {
        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerTarget = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user", directory: "/tmp/target");
        var ownerRepo = new SessionCallbackRepository(factory, new TestUserContext("owner-user"));
        var callback = new SessionCallback
        {
            Id = Guid.NewGuid().ToString(),
            SourceSessionId = ownerGraph.Session.Id,
            TargetSessionId = ownerTarget.Session.Id,
            TargetInstanceId = ownerTarget.Instance.Id,
            Status = status,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        await ownerRepo.InsertAsync(callback);
        return callback;
    }

    [Fact]
    public async Task A_callback_moves_from_pending_to_started_to_fired_once()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;
        var callback = await InsertOwnersCallbackAsync(factory);
        var repo = new SessionCallbackRepository(factory, new TestUserContext("owner-user"));

        (await repo.GetStartedAsync()).ShouldBeEmpty();
        (await repo.MarkFiredAsync(callback.Id)).ShouldBeFalse();

        (await repo.MarkSourceStartedAsync(callback.SourceSessionId)).ShouldBe(1);
        (await repo.MarkSourceStartedAsync(callback.SourceSessionId)).ShouldBe(0);
        var started = (await repo.GetStartedAsync()).ShouldHaveSingleItem();
        started.Id.ShouldBe(callback.Id);
        started.Status.ShouldBe(SessionCallbackStatuses.Started);
        (await repo.GetOwnersWithStartedCallbacksAsync()).ShouldBe(["owner-user"]);

        (await repo.MarkFiredAsync(callback.Id)).ShouldBeTrue();
        (await repo.MarkFiredAsync(callback.Id)).ShouldBeFalse();
        (await repo.GetStartedAsync()).ShouldBeEmpty();
        (await repo.GetOwnersWithStartedCallbacksAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStartedAsync_DoesNotReturnOtherUsersCallbacks()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;
        await InsertOwnersCallbackAsync(factory, SessionCallbackStatuses.Started);

        (await repo.GetStartedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkSourceStartedAsync_DoesNotMoveOtherUsersCallbacks()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;
        var callback = await InsertOwnersCallbackAsync(factory);

        (await repo.MarkSourceStartedAsync(callback.SourceSessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task MarkFiredAsync_ReturnsFalseForOtherUsersCallback()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;
        var callback = await InsertOwnersCallbackAsync(factory, SessionCallbackStatuses.Started);

        (await repo.MarkFiredAsync(callback.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task GetOwnersWithStartedCallbacksAsync_FindsEveryUsersCallbacks()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;
        await InsertOwnersCallbackAsync(factory, SessionCallbackStatuses.Started);

        // The poll has no user of its own; it asks for everyone's.
        (await repo.GetOwnersWithStartedCallbacksAsync()).ShouldBe(["owner-user"]);
    }
}
