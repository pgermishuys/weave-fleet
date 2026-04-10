using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperSessionSourceUsageRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperSessionSourceUsageRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperSessionSourceUsageRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    [Fact]
    public async Task InsertAndListBySessionIdAsync_ReturnsPersistedUsage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var userContext = new TestUserContext();
        var projectRepo = new DapperProjectRepository(factory, userContext);
        var workspaceRepo = new DapperWorkspaceRepository(factory, userContext);
        var instanceRepo = new DapperInstanceRepository(factory, userContext);
        var sessionRepo = new DapperSessionRepository(factory, userContext);

        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Project",
            Type = "user",
            Position = 0,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await projectRepo.InsertAsync(project);

        var workspace = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/usage-workspace",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await workspaceRepo.InsertAsync(workspace);

        var instance = new Instance
        {
            Id = Guid.NewGuid().ToString(),
            Port = 0,
            Directory = "/tmp/usage-workspace",
            Url = "http://localhost",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await instanceRepo.InsertAsync(instance);

        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            ProjectId = project.Id,
            OpencodeSessionId = "oc-usage-1",
            Title = "Usage Session",
            Status = "active",
            Directory = workspace.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await sessionRepo.InsertAsync(session);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = session.Id,
            WorkspaceId = workspace.Id,
            ProviderId = "builtin.local",
            SourceType = "directory",
            ActionId = "start-session",
            ResourceId = workspace.Directory,
            ResourceUrl = "file:///tmp/usage-workspace",
            Title = "usage-workspace",
            Summary = "redacted",
            CreatedAt = "2026-01-02T00:00:00.0000000Z"
        });

        var usages = await repo.ListBySessionIdAsync(session.Id);

        usages.Count.ShouldBe(1);
        usages[0].ProviderId.ShouldBe("builtin.local");
        usages[0].SourceType.ShouldBe("directory");
        usages[0].ActionId.ShouldBe("start-session");
        usages[0].Summary.ShouldBe("redacted");
    }

    [Fact]
    public async Task ListBySessionIdAsync_DoesNotReturnOtherUsersUsage()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new DapperSessionSourceUsageRepository(factory, new TestUserContext("owner-user"));
        await ownerRepo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = ownerGraph.Session.Id,
            WorkspaceId = ownerGraph.Workspace.Id,
            ProviderId = "provider",
            SourceType = "repository",
            ActionId = "attach",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var repo = new DapperSessionSourceUsageRepository(factory, new TestUserContext());
        var usages = await repo.ListBySessionIdAsync(ownerGraph.Session.Id);

        usages.ShouldBeEmpty();
    }

    [Fact]
    public async Task InsertAsync_DoesNotWriteToOtherUsersSession()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new DapperSessionSourceUsageRepository(factory, new TestUserContext());

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = ownerGraph.Session.Id,
            WorkspaceId = ownerGraph.Workspace.Id,
            ProviderId = "provider",
            SourceType = "repository",
            ActionId = "attach",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var ownerRepo = new DapperSessionSourceUsageRepository(factory, new TestUserContext("owner-user"));
        var usages = await ownerRepo.ListBySessionIdAsync(ownerGraph.Session.Id);
        usages.ShouldBeEmpty();
    }
}
