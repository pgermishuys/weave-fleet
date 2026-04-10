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

        retrieved.ShouldNotBeNull();
        retrieved.Directory.ShouldBe(ws.Directory);
        retrieved.IsolationStrategy.ShouldBe("existing");
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

        found.ShouldNotBeNull();
        found.Id.ShouldBe(ws.Id);
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

        retrieved.ShouldNotBeNull();
        retrieved.CleanedUpAt.ShouldNotBeNull();
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

        retrieved.ShouldNotBeNull();
        retrieved.DisplayName.ShouldBe("My Project");
    }

    [Fact]
    public async Task InsertAndGetById_PersistsSourceMetadata()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/source-meta",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            SourceProviderId = "builtin.local",
            SourceType = "directory",
            SourceResourceId = "/tmp/source-meta",
            SourceResourceUrl = "file:///tmp/source-meta",
            SourceTitle = "source-meta",
            SourceSummary = "redacted summary",
            SourceResolvedAt = DateTime.UtcNow.ToString("O")
        };

        await repo.InsertAsync(ws);
        var retrieved = await repo.GetByIdAsync(ws.Id);

        retrieved.ShouldNotBeNull();
        retrieved.SourceProviderId.ShouldBe("builtin.local");
        retrieved.SourceType.ShouldBe("directory");
        retrieved.SourceResourceId.ShouldBe("/tmp/source-meta");
        retrieved.SourceResourceUrl.ShouldBe("file:///tmp/source-meta");
        retrieved.SourceTitle.ShouldBe("source-meta");
        retrieved.SourceSummary.ShouldBe("redacted summary");
        retrieved.SourceResolvedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpdateSourceMetadataAsync_ChangesSourceFields()
    {
        var (conn, repo) = await CreateAsync();
        using var _ = conn;

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/update-source",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await repo.InsertAsync(ws);
        await repo.UpdateSourceMetadataAsync(
            ws.Id,
            "builtin.repository",
            "repository",
            "repo-1",
            "https://example.test/repo-1",
            "Repo 1",
            "summary",
            "2026-01-01T00:00:00.0000000Z");

        var retrieved = await repo.GetByIdAsync(ws.Id);

        retrieved.ShouldNotBeNull();
        retrieved.SourceProviderId.ShouldBe("builtin.repository");
        retrieved.SourceType.ShouldBe("repository");
        retrieved.SourceResourceId.ShouldBe("repo-1");
        retrieved.SourceResourceUrl.ShouldBe("https://example.test/repo-1");
        retrieved.SourceTitle.ShouldBe("Repo 1");
        retrieved.SourceSummary.ShouldBe("summary");
        retrieved.SourceResolvedAt.ShouldBe("2026-01-01T00:00:00.0000000Z");
    }
}
