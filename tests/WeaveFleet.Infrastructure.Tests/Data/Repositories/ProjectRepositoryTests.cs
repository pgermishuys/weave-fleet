using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class ProjectRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, ProjectRepository Repo)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new ProjectRepository(factory, new TestUserContext());
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
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };

        await repo.InsertAsync(project);
        var retrieved = await repo.GetByIdAsync(project.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe(project.Name);
        retrieved.Description.ShouldBe(project.Description);
        retrieved.Type.ShouldBe("user");
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
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(scratch);

        var retrieved = await repo.GetScratchProjectAsync();

        retrieved.ShouldNotBeNull();
        retrieved.Type.ShouldBe("scratch");
    }

    [Fact]
    public async Task ListAsync_ReturnsAllProjectsOrderedByPosition()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        await repo.InsertAsync(new Project { Id = Guid.NewGuid().ToString(), Name = "B", Position = 2, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O"), UserId = TestUserContext.DefaultUserId });
        await repo.InsertAsync(new Project { Id = Guid.NewGuid().ToString(), Name = "A", Position = 1, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O"), UserId = TestUserContext.DefaultUserId });

        var list = await repo.ListAsync();

        list.Count.ShouldBe(2);
        list[0].Name.ShouldBe("A");
        list[1].Name.ShouldBe("B");
    }

    [Fact]
    public async Task DeleteAsync_RemovesProject()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var project = new Project { Id = Guid.NewGuid().ToString(), Name = "ToDelete", Position = 0, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O"), UserId = TestUserContext.DefaultUserId };
        await repo.InsertAsync(project);

        await repo.DeleteAsync(project.Id);

        var retrieved = await repo.GetByIdAsync(project.Id);
        retrieved.ShouldBeNull();
    }

    [Fact]
    public async Task GetSessionCountAsync_ReturnsZeroWhenNoSessions()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var project = new Project { Id = Guid.NewGuid().ToString(), Name = "P", Position = 0, Type = "user", CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O"), UserId = TestUserContext.DefaultUserId };
        await repo.InsertAsync(project);

        var count = await repo.GetSessionCountAsync(project.Id);
        count.ShouldBe(0);
    }
}
