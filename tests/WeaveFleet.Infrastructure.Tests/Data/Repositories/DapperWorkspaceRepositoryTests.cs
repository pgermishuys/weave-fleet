using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperWorkspaceRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperWorkspaceRepository Repo)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperWorkspaceRepository(factory);
        return (keeper, repo);
    }

    [Fact]
    public async Task InsertAndGetById_ReturnsWorkspace()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/test",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await repo.InsertAsync(ws);
        var retrieved = await repo.GetByIdAsync(ws.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(ws.Directory, retrieved.Directory);
        Assert.Equal("existing", retrieved.IsolationStrategy);
    }

    [Fact]
    public async Task GetByDirectoryAsync_FindsMatchingWorkspace()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/home/user/project",
            IsolationStrategy = "worktree",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        await repo.InsertAsync(ws);

        var found = await repo.GetByDirectoryAsync("/home/user/project", "worktree");

        Assert.NotNull(found);
        Assert.Equal(ws.Id, found.Id);
    }

    [Fact]
    public async Task MarkCleanedAsync_SetsCleanedUpAt()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/cleanup",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        await repo.InsertAsync(ws);

        await repo.MarkCleanedAsync(ws.Id);
        var retrieved = await repo.GetByIdAsync(ws.Id);

        Assert.NotNull(retrieved!.CleanedUpAt);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_ChangesName()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/named",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        await repo.InsertAsync(ws);

        await repo.UpdateDisplayNameAsync(ws.Id, "My Project");
        var retrieved = await repo.GetByIdAsync(ws.Id);

        Assert.Equal("My Project", retrieved!.DisplayName);
    }
}
