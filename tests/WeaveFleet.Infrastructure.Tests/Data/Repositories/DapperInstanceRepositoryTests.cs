using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperInstanceRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperInstanceRepository Repo, WeaveFleet.Application.Data.IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperInstanceRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    private static Instance MakeInstance(int port = 8080) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Port = port,
        Directory = "/tmp/inst",
        Url = $"http://localhost:{port}",
        Status = "running",
        CreatedAt = DateTime.UtcNow.ToString("O"),
        UserId = TestUserContext.DefaultUserId
    };

    [Fact]
    public async Task InsertAndGetById_ReturnsInstance()
    {
        var (conn, repo, _) = await CreateAsync();
        using var _ = conn;

        var inst = MakeInstance();
        await repo.InsertAsync(inst);
        var retrieved = await repo.GetByIdAsync(inst.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Port.ShouldBe(inst.Port);
        retrieved.Status.ShouldBe("running");
    }

    [Fact]
    public async Task GetRunningAsync_OnlyReturnsRunning()
    {
        var (conn, repo, _) = await CreateAsync();
        using var _ = conn;

        var running = MakeInstance(8001);
        var stopped = MakeInstance(8002);
        stopped.Status = "stopped";
        stopped.StoppedAt = DateTime.UtcNow.ToString("O");

        await repo.InsertAsync(running);
        await repo.InsertAsync(stopped);

        var results = await repo.GetRunningAsync();
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(running.Id);
    }

    [Fact]
    public async Task MarkAllStoppedAsync_StopsAllRunning()
    {
        var (conn, repo, _) = await CreateAsync();
        using var _ = conn;

        await repo.InsertAsync(MakeInstance(9001));
        await repo.InsertAsync(MakeInstance(9002));

        var count = await repo.MarkAllStoppedAsync(DateTime.UtcNow.ToString("O"));

        count.ShouldBe(2);
        var running = await repo.GetRunningAsync();
        running.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        var (conn, repo, _) = await CreateAsync();
        using var _ = conn;

        var inst = MakeInstance();
        await repo.InsertAsync(inst);

        await repo.UpdateStatusAsync(inst.Id, "stopped", DateTime.UtcNow.ToString("O"));

        var retrieved = await repo.GetByIdAsync(inst.Id);
        retrieved.ShouldNotBeNull();
        retrieved.Status.ShouldBe("stopped");
        retrieved.StoppedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_DoesNotUpdateOtherUsersInstance()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new DapperInstanceRepository(factory, new TestUserContext());

        await repo.UpdateStatusAsync(ownerGraph.Instance.Id, "stopped", DateTime.UtcNow.ToString("O"));

        var ownerRepo = new DapperInstanceRepository(factory, new TestUserContext("owner-user"));
        var instance = await ownerRepo.GetByIdAsync(ownerGraph.Instance.Id);
        instance.ShouldNotBeNull();
        instance.Status.ShouldBe("running");
    }
}
