using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class SessionSourceUsageRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, SessionSourceUsageRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new SessionSourceUsageRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    [Fact]
    public async Task InsertAndListBySessionIdAsync_ReturnsPersistedUsage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var userContext = new TestUserContext();
        var projectRepo = new ProjectRepository(factory, userContext);
        var workspaceRepo = new WorkspaceRepository(factory, userContext);
        var instanceRepo = new InstanceRepository(factory, userContext);
        var sessionRepo = new SessionRepository(factory, userContext);

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
        var ownerRepo = new SessionSourceUsageRepository(factory, new TestUserContext("owner-user"));
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

        var repo = new SessionSourceUsageRepository(factory, new TestUserContext());
        var usages = await repo.ListBySessionIdAsync(ownerGraph.Session.Id);

        usages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPrimaryBySessionIdAsync_ReturnsEarliestStartSessionUsage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var graph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = graph.Session.Id,
            WorkspaceId = graph.Workspace.Id,
            ProviderId = "provider",
            SourceType = "repository",
            ActionId = "add-to-session",
            CreatedAt = "2026-01-03T00:00:00.0000000Z"
        });

        var expected = new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = graph.Session.Id,
            WorkspaceId = graph.Workspace.Id,
            ProviderId = "provider-a",
            SourceType = "repository",
            ActionId = "start-session",
            CreatedAt = "2026-01-01T00:00:00.0000000Z"
        };

        await repo.InsertAsync(expected);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = graph.Session.Id,
            WorkspaceId = graph.Workspace.Id,
            ProviderId = "provider-b",
            SourceType = "directory",
            ActionId = "start-session",
            CreatedAt = "2026-01-02T00:00:00.0000000Z"
        });

        var usage = await repo.GetPrimaryBySessionIdAsync(graph.Session.Id);

        usage.ShouldNotBeNull();
        usage.Id.ShouldBe(expected.Id);
        usage.ProviderId.ShouldBe("provider-a");
    }

    [Fact]
    public async Task GetPrimaryBySessionIdAsync_ReturnsNullWhenNoStartSessionUsageExists()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var graph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = graph.Session.Id,
            WorkspaceId = graph.Workspace.Id,
            ProviderId = "provider",
            SourceType = "repository",
            ActionId = "add-to-session",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var usage = await repo.GetPrimaryBySessionIdAsync(graph.Session.Id);

        usage.ShouldBeNull();
    }

    [Fact]
    public async Task GetPrimaryBySessionIdsAsync_ReturnsEarliestStartSessionUsagePerSession()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var firstGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var secondGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);

        var firstExpected = new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = firstGraph.Session.Id,
            WorkspaceId = firstGraph.Workspace.Id,
            ProviderId = "provider-a",
            SourceType = "repository",
            ActionId = "start-session",
            CreatedAt = "2026-01-01T00:00:00.0000000Z"
        };

        var secondExpected = new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = secondGraph.Session.Id,
            WorkspaceId = secondGraph.Workspace.Id,
            ProviderId = "provider-b",
            SourceType = "directory",
            ActionId = "start-session",
            CreatedAt = "2026-01-02T00:00:00.0000000Z"
        };

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = firstGraph.Session.Id,
            WorkspaceId = firstGraph.Workspace.Id,
            ProviderId = "provider-late",
            SourceType = "directory",
            ActionId = "start-session",
            CreatedAt = "2026-01-03T00:00:00.0000000Z"
        });

        await repo.InsertAsync(firstExpected);
        await repo.InsertAsync(secondExpected);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = secondGraph.Session.Id,
            WorkspaceId = secondGraph.Workspace.Id,
            ProviderId = "provider-non-primary",
            SourceType = "repository",
            ActionId = "add-to-session",
            CreatedAt = "2026-01-01T00:00:00.0000000Z"
        });

        var usages = await repo.GetPrimaryBySessionIdsAsync([firstGraph.Session.Id, secondGraph.Session.Id]);

        usages.Count.ShouldBe(2);
        usages[firstGraph.Session.Id].Id.ShouldBe(firstExpected.Id);
        usages[secondGraph.Session.Id].Id.ShouldBe(secondExpected.Id);
    }

    [Fact]
    public async Task GetPrimaryBySessionIdsAsync_DoesNotReturnOtherUsersUsage()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var ownerRepo = new SessionSourceUsageRepository(factory, new TestUserContext("owner-user"));
        await ownerRepo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = ownerGraph.Session.Id,
            WorkspaceId = ownerGraph.Workspace.Id,
            ProviderId = "provider",
            SourceType = "repository",
            ActionId = "start-session",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var repo = new SessionSourceUsageRepository(factory, new TestUserContext());
        var usages = await repo.GetPrimaryBySessionIdsAsync([ownerGraph.Session.Id]);

        usages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPrimaryBySessionIdsAsync_ReturnsOnlySessionsWithStartSessionUsageWhenBatchIsMixed()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionWithOrigin = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var sessionWithoutOrigin = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);

        var expected = new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionWithOrigin.Session.Id,
            WorkspaceId = sessionWithOrigin.Workspace.Id,
            ProviderId = "provider-a",
            SourceType = "repository",
            ActionId = "start-session",
            Title = "origin-title",
            CreatedAt = "2026-01-01T00:00:00.0000000Z"
        };

        await repo.InsertAsync(expected);

        await repo.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionWithoutOrigin.Session.Id,
            WorkspaceId = sessionWithoutOrigin.Workspace.Id,
            ProviderId = "provider-b",
            SourceType = "directory",
            ActionId = "add-to-session",
            Title = "non-primary-origin",
            CreatedAt = "2026-01-02T00:00:00.0000000Z"
        });

        var usages = await repo.GetPrimaryBySessionIdsAsync([sessionWithOrigin.Session.Id, sessionWithoutOrigin.Session.Id]);

        usages.Count.ShouldBe(1);
        usages[sessionWithOrigin.Session.Id].Id.ShouldBe(expected.Id);
        usages.ContainsKey(sessionWithoutOrigin.Session.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task InsertAsync_DoesNotWriteToOtherUsersSession()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new SessionSourceUsageRepository(factory, new TestUserContext());

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

        var ownerRepo = new SessionSourceUsageRepository(factory, new TestUserContext("owner-user"));
        var usages = await ownerRepo.ListBySessionIdAsync(ownerGraph.Session.Id);
        usages.ShouldBeEmpty();
    }
}
