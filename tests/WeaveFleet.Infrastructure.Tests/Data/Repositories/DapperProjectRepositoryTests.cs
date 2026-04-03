using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperProjectRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperProjectRepository Repo)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperProjectRepository(factory);
        return (keeper, repo);
    }

    [Fact]
    public async Task InsertAndGetById_ReturnsProject()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Project",
            Description = "A test",
            Type = "user",
            Position = 1,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };

        await repo.InsertAsync(project);
        var retrieved = await repo.GetByIdAsync(project.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(project.Name, retrieved.Name);
        Assert.Equal(project.Description, retrieved.Description);
        Assert.Equal("user", retrieved.Type);
    }

    [Fact]
    public async Task GetScratchProject_ReturnsScratchType()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var scratch = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Scratch",
            Type = "scratch",
            Position = 0,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        await repo.InsertAsync(scratch);

        var retrieved = await repo.GetScratchProjectAsync();

        Assert.NotNull(retrieved);
        Assert.Equal("scratch", retrieved.Type);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllProjectsOrderedByPosition()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        await repo.InsertAsync(new Project { Id = Guid.NewGuid().ToString(), Name = "B", Position = 2, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O") });
        await repo.InsertAsync(new Project { Id = Guid.NewGuid().ToString(), Name = "A", Position = 1, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O") });

        var list = await repo.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("A", list[0].Name);
        Assert.Equal("B", list[1].Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesProject()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var project = new Project { Id = Guid.NewGuid().ToString(), Name = "ToDelete", Position = 0, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O") };
        await repo.InsertAsync(project);

        await repo.DeleteAsync(project.Id);

        var retrieved = await repo.GetByIdAsync(project.Id);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task GetSessionCountAsync_ReturnsZeroWhenNoSessions()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var project = new Project { Id = Guid.NewGuid().ToString(), Name = "P", Position = 0, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O") };
        await repo.InsertAsync(project);

        var count = await repo.GetSessionCountAsync(project.Id);
        Assert.Equal(0, count);
    }
}
