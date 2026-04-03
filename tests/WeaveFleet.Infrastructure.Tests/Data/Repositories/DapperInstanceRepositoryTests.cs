using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperInstanceRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperInstanceRepository Repo)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperInstanceRepository(factory);
        return (keeper, repo);
    }

    private static Instance MakeInstance(int port = 8080) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Port = port,
        Directory = "/tmp/inst",
        Url = $"http://localhost:{port}",
        Status = "running",
        CreatedAt = DateTime.UtcNow.ToString("O")
    };

    [Fact]
    public async Task InsertAndGetById_ReturnsInstance()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var inst = MakeInstance();
        await repo.InsertAsync(inst);
        var retrieved = await repo.GetByIdAsync(inst.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(inst.Port, retrieved.Port);
        Assert.Equal("running", retrieved.Status);
    }

    [Fact]
    public async Task GetRunningAsync_OnlyReturnsRunning()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var running = MakeInstance(8001);
        var stopped = MakeInstance(8002);
        stopped.Status = "stopped";
        stopped.StoppedAt = DateTime.UtcNow.ToString("O");

        await repo.InsertAsync(running);
        await repo.InsertAsync(stopped);

        var results = await repo.GetRunningAsync();
        Assert.Single(results);
        Assert.Equal(running.Id, results[0].Id);
    }

    [Fact]
    public async Task MarkAllStoppedAsync_StopsAllRunning()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        await repo.InsertAsync(MakeInstance(9001));
        await repo.InsertAsync(MakeInstance(9002));

        var count = await repo.MarkAllStoppedAsync(DateTime.UtcNow.ToString("O"));

        Assert.Equal(2, count);
        var running = await repo.GetRunningAsync();
        Assert.Empty(running);
    }

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var inst = MakeInstance();
        await repo.InsertAsync(inst);

        await repo.UpdateStatusAsync(inst.Id, "stopped", DateTime.UtcNow.ToString("O"));

        var retrieved = await repo.GetByIdAsync(inst.Id);
        Assert.Equal("stopped", retrieved!.Status);
        Assert.NotNull(retrieved.StoppedAt);
    }
}
