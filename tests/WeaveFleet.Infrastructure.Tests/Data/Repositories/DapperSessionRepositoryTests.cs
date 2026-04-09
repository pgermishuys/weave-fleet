using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperSessionRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, DapperSessionRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperSessionRepository(factory);
        return (keeper, repo, factory);
    }

    private static async Task<(Workspace W, Instance I)> InsertDependenciesAsync(IDbConnectionFactory factory)
    {
        var wsRepo = new DapperWorkspaceRepository(factory);
        var instRepo = new DapperInstanceRepository(factory);

        var ws = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/ws",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        var inst = new Instance
        {
            Id = Guid.NewGuid().ToString(),
            Port = 9000,
            Directory = "/tmp/ws",
            Url = "http://localhost:9000",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await wsRepo.InsertAsync(ws);
        await instRepo.InsertAsync(inst);
        return (ws, inst);
    }

    [Fact]
    public async Task InsertAndGetById_ReturnsSession()
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
            TotalCost = 0
        };

        await repo.InsertAsync(session);
        var retrieved = await repo.GetByIdAsync(session.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Title.ShouldBe(session.Title);
        retrieved.Status.ShouldBe("active");
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
            CreatedAt = DateTime.UtcNow.ToString("O")
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
            IsHidden = true
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
                CreatedAt = DateTime.UtcNow.ToString("O")
            });
        }

        var list = await repo.ListAsync();
        list.Count.ShouldBe(3);
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
            CreatedAt = DateTime.UtcNow.ToString("O")
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
            CreatedAt = DateTime.UtcNow.ToString("O")
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
        await repo.InsertAsync(new Session { Id = Guid.NewGuid().ToString(), WorkspaceId = ws.Id, InstanceId = inst.Id, OpencodeSessionId = "oc-t1", Title = "T1", Status = "active", Directory = "/tmp", CreatedAt = DateTime.UtcNow.ToString("O"), TotalTokens = 50, TotalCost = 0.05 });
        await repo.InsertAsync(new Session { Id = Guid.NewGuid().ToString(), WorkspaceId = ws.Id, InstanceId = inst.Id, OpencodeSessionId = "oc-t2", Title = "T2", Status = "active", Directory = "/tmp", CreatedAt = DateTime.UtcNow.ToString("O"), TotalTokens = 150, TotalCost = 0.15 });

        var (totalTokens, totalCost) = await repo.GetFleetTokenTotalsAsync();

        totalTokens.ShouldBe(200);
        totalCost.ShouldBe(0.20, 0.00001);
    }
}
