using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class SessionRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, SessionRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new SessionRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    private static async Task<(Workspace W, Instance I)> InsertDependenciesAsync(IDbConnectionFactory factory)
    {
        var userContext = new TestUserContext();
        var wsRepo = new WorkspaceRepository(factory, userContext);
        var instRepo = new InstanceRepository(factory, userContext);

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/ws",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        var inst = new Instance
        {
            Id = Guid.NewGuid().ToString(),
            Port = 9000,
            Directory = "/tmp/ws",
            Url = "http://localhost:9000",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };

        await wsRepo.InsertAsync(ws);
        await instRepo.InsertAsync(inst);
        return (ws, inst);
    }

    [Fact]
    public async Task insert_and_get_by_id_returns_session()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-1",
            Title = "Test Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            TotalTokens = 0,
            UserId = TestUserContext.DefaultUserId,
            TotalCost = 0,
            GitBaselineRef = "main",
            GitRepoRoot = "/tmp/ws"
        };

        await repo.InsertAsync(session);
        var retrieved = await repo.GetByIdAsync(session.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Title.ShouldBe(session.Title);
        retrieved.Status.ShouldBe("active");
        retrieved.RetentionStatus.ShouldBe("active");
        retrieved.ArchivedAt.ShouldBeNull();
        retrieved.GitBaselineRef.ShouldBe("main");
        retrieved.GitRepoRoot.ShouldBe("/tmp/ws");
    }

    [Fact]
    public async Task insert_and_get_by_id_returns_null_git_baseline_metadata_when_not_set()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-null-git-baseline",
            Title = "Null Git Baseline Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };

        await repo.InsertAsync(session);
        var retrieved = await repo.GetByIdAsync(session.Id);

        retrieved.ShouldNotBeNull();
        retrieved.GitBaselineRef.ShouldBeNull();
        retrieved.GitRepoRoot.ShouldBeNull();
    }

    [Fact]
    public async Task GetByHarnessIdAsync_FindsByOpencodeId()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-harness-42",
            Title = "Harness Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(session);

        var found = await repo.GetByHarnessIdAsync("oc-harness-42");

        found.ShouldNotBeNull();
        found.Id.ShouldBe(session.Id);
    }

    [Fact]
    public async Task InsertAndGetById_PersistsHiddenFlag()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-hidden-1",
            Title = "Hidden Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            IsHidden = true,
            UserId = TestUserContext.DefaultUserId
        };

        await repo.InsertAsync(session);
        var retrieved = await repo.GetByIdAsync(session.Id);

        retrieved.ShouldNotBeNull();
        retrieved.IsHidden.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSessions()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);

        for (var i = 0; i < 3; i++)
        {
            await repo.InsertAsync(new Session
            {
                Id = Guid.NewGuid().ToString(),
                WorkspaceId = ws.Id,
                InstanceId = inst.Id,
                OpencodeSessionId = $"oc-{i}",
                Title = $"Session {i}",
                Status = "active",
                Directory = "/tmp/ws",
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UserId = TestUserContext.DefaultUserId
            });
        }

        var list = await repo.ListAsync();
        list.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ListAsync_WithRetentionFilter_ReturnsMatchingSessions()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var archivedSessionId = Guid.NewGuid().ToString();
        await repo.InsertAsync(new Session
        {
            Id = archivedSessionId,
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-archived",
            Title = "Archived Session",
            Status = "stopped",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.InsertAsync(new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-active",
            Title = "Active Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.ArchiveAsync(archivedSessionId, DateTime.UtcNow.ToString("O"));

        var archived = await repo.ListAsync(100, 0, statuses: null, projectId: null, retentionStatuses: ["archived"]);
        var active = await repo.ListAsync(100, 0, statuses: null, projectId: null, retentionStatuses: ["active"]);

        archived.Select(session => session.Id).ShouldBe([archivedSessionId]);
        active.Count.ShouldBe(1);
        active[0].RetentionStatus.ShouldBe("active");
    }

    [Fact]
    public async Task ListAsync_WithNoRetentionFilter_ReturnsActiveAndArchivedSessions()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var archivedSessionId = Guid.NewGuid().ToString();

        await repo.InsertAsync(new Session
        {
            Id = archivedSessionId,
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-all-archived",
            Title = "Archived Session",
            Status = "stopped",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.InsertAsync(new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-all-active",
            Title = "Active Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.ArchiveAsync(archivedSessionId, DateTime.UtcNow.ToString("O"));

        var allSessions = await repo.ListAsync(100, 0, statuses: null, projectId: null, retentionStatuses: null);

        allSessions.Count.ShouldBe(2);
        allSessions.Select(session => session.RetentionStatus).OrderBy(status => status).ShouldBe(["active", "archived"]);
    }

    [Fact]
    public async Task ArchiveAsync_UpdatesRetentionFields()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-archive",
            Title = "Archive Test",
            Status = "stopped",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(session);

        var archivedAt = DateTime.UtcNow.ToString("O");
        await repo.ArchiveAsync(session.Id, archivedAt);

        var updated = await repo.GetByIdAsync(session.Id);
        updated.ShouldNotBeNull();
        updated.RetentionStatus.ShouldBe("archived");
        updated.ArchivedAt.ShouldBe(archivedAt);
    }

    [Fact]
    public async Task UnarchiveAsync_RestoresActiveRetentionAndClearsArchivedAt()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-unarchive",
            Title = "Unarchive Test",
            Status = "stopped",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(session);

        await repo.ArchiveAsync(session.Id, DateTime.UtcNow.ToString("O"));
        await repo.UnarchiveAsync(session.Id);

        var updated = await repo.GetByIdAsync(session.Id);
        updated.ShouldNotBeNull();
        updated.RetentionStatus.ShouldBe("active");
        updated.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public async Task CountAsync_WithRetentionFilter_ReturnsMatchingCount()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var archivedSessionId = Guid.NewGuid().ToString();

        await repo.InsertAsync(new Session
        {
            Id = archivedSessionId,
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-count-archived",
            Title = "Archived Count",
            Status = "stopped",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.InsertAsync(new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-count-active",
            Title = "Active Count",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        });

        await repo.ArchiveAsync(archivedSessionId, DateTime.UtcNow.ToString("O"));

        var archivedCount = await repo.CountAsync(statuses: null, retentionStatuses: ["archived"]);
        var activeCount = await repo.CountAsync(statuses: null, retentionStatuses: ["active"]);

        archivedCount.ShouldBe(1);
        activeCount.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-status",
            Title = "Status Test",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(session);

        await repo.UpdateStatusAsync(session.Id, "stopped", DateTime.UtcNow.ToString("O"));

        var updated = await repo.GetByIdAsync(session.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("stopped");
        updated.StoppedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task IncrementTokensAsync_AccumulatesTokens()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-tokens",
            Title = "Token Test",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId
        };
        await repo.InsertAsync(session);

        await repo.IncrementTokensAsync(session.Id, 100, 0.01);
        var result = await repo.IncrementTokensAsync(session.Id, 200, 0.02);

        result.ShouldNotBeNull();
        result.Value.TotalTokens.ShouldBe(300);
        result.Value.TotalCost.ShouldBe(0.03, 0.00001);
    }

    [Fact]
    public async Task GetFleetTokenTotalsAsync_ReturnsAggregates()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var (ws, inst) = await InsertDependenciesAsync(factory);
        await repo.InsertAsync(new Session { Id = Guid.NewGuid().ToString(), WorkspaceId = ws.Id, InstanceId = inst.Id, OpencodeSessionId = "oc-t1", Title = "T1", Status = "active", Directory = "/tmp", CreatedAt = DateTime.UtcNow.ToString("O"), TotalTokens = 50, TotalCost = 0.05, UserId = TestUserContext.DefaultUserId });
        await repo.InsertAsync(new Session { Id = Guid.NewGuid().ToString(), WorkspaceId = ws.Id, InstanceId = inst.Id, OpencodeSessionId = "oc-t2", Title = "T2", Status = "active", Directory = "/tmp", CreatedAt = DateTime.UtcNow.ToString("O"), TotalTokens = 150, TotalCost = 0.15, UserId = TestUserContext.DefaultUserId });

        var (totalTokens, totalCost) = await repo.GetFleetTokenTotalsAsync();

        totalTokens.ShouldBe(200);
        totalCost.ShouldBe(0.20, 0.00001);
    }

    [Fact]
    public async Task UpdateStatusAsync_DoesNotUpdateOtherUsersSession()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new SessionRepository(factory, new TestUserContext());

        await repo.UpdateStatusAsync(ownerGraph.Session.Id, "stopped", DateTime.UtcNow.ToString("O"));

        var ownerRepo = new SessionRepository(factory, new TestUserContext("owner-user"));
        var session = await ownerRepo.GetByIdAsync(ownerGraph.Session.Id);
        session.ShouldNotBeNull();
        session.Status.ShouldBe("active");
    }

    [Fact]
    public async Task IncrementTokensAsync_ReturnsNullForOtherUsersSession()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new SessionRepository(factory, new TestUserContext());

        var result = await repo.IncrementTokensAsync(ownerGraph.Session.Id, 50, 1.25);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetForInstanceAsync_DoesNotReturnOtherUsersSessions()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var ownerGraph = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "owner-user");
        var repo = new SessionRepository(factory, new TestUserContext());

        var sessions = await repo.GetForInstanceAsync(ownerGraph.Instance.Id);

        sessions.ShouldBeEmpty();
    }
}
