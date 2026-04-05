using Dapper;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperMessageRepositoryTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<(SqliteConnection Keeper, DapperMessageRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new DapperMessageRepository(factory);
        return (keeper, repo, factory);
    }

    /// <summary>
    /// Inserts a session row so foreign-key constraints on messages are satisfied.
    /// </summary>
    private static async Task<string> SeedSessionAsync(IDbConnectionFactory factory, string? sessionId = null)
    {
        var wsRepo = new DapperWorkspaceRepository(factory);
        var instRepo = new DapperInstanceRepository(factory);
        var sessionRepo = new DapperSessionRepository(factory);

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
            Port = 9001,
            Directory = "/tmp/ws",
            Url = "http://localhost:9001",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        var session = new Session
        {
            Id = sessionId ?? Guid.NewGuid().ToString(),
            WorkspaceId = ws.Id,
            InstanceId = inst.Id,
            OpencodeSessionId = "oc-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Test Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await wsRepo.InsertAsync(ws);
        await instRepo.InsertAsync(inst);
        await sessionRepo.InsertAsync(session);

        return session.Id;
    }

    private static PersistedMessage MakeMessage(string sessionId, string? id = null, string? timestamp = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"hello"}]""",
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_InsertsNewMessage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var msg = MakeMessage(sessionId);

        await repo.UpsertAsync(msg);

        var count = await repo.CountBySessionAsync(sessionId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertAsync_ReplacesExistingMessage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var id = Guid.NewGuid().ToString();

        // First upsert — initial skeleton
        await repo.UpsertAsync(new PersistedMessage
        {
            Id = id,
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        // Second upsert — complete response
        await repo.UpsertAsync(new PersistedMessage
        {
            Id = id,
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"complete"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        var messages = await repo.GetBySessionAsync(sessionId, 10, null);
        Assert.Single(messages);
        Assert.Contains("complete", messages[0].PartsJson);
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsMessagesInAscendingTimestampOrder()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Insert out of order
        await repo.UpsertAsync(MakeMessage(sessionId, timestamp: base_.AddMinutes(3).ToString("O")));
        await repo.UpsertAsync(MakeMessage(sessionId, timestamp: base_.AddMinutes(1).ToString("O")));
        await repo.UpsertAsync(MakeMessage(sessionId, timestamp: base_.AddMinutes(2).ToString("O")));

        var messages = await repo.GetBySessionAsync(sessionId, 10, null);

        Assert.Equal(3, messages.Count);
        Assert.True(string.Compare(messages[0].Timestamp, messages[1].Timestamp, StringComparison.Ordinal) <= 0);
        Assert.True(string.Compare(messages[1].Timestamp, messages[2].Timestamp, StringComparison.Ordinal) <= 0);
    }

    [Fact]
    public async Task GetBySessionAsync_RespectsLimit()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            await repo.UpsertAsync(MakeMessage(sessionId, timestamp: base_.AddMinutes(i).ToString("O")));

        var page = await repo.GetBySessionAsync(sessionId, 3, null);

        Assert.Equal(3, page.Count);
    }

    [Fact]
    public async Task GetBySessionAsync_WithCursor_ReturnsOlderMessages()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString()).ToList();

        // Insert in ascending time order (t0..t4)
        for (var i = 0; i < 5; i++)
            await repo.UpsertAsync(MakeMessage(sessionId, id: ids[i], timestamp: base_.AddMinutes(i).ToString("O")));

        // Request page before the 3rd message (ids[2] = t2) — should get t0 and t1
        var page = await repo.GetBySessionAsync(sessionId, 10, ids[2]);

        Assert.Equal(2, page.Count);
        Assert.All(page, m =>
            Assert.True(string.Compare(m.Timestamp, base_.AddMinutes(2).ToString("O"), StringComparison.Ordinal) < 0));
    }

    [Fact]
    public async Task GetBySessionAsync_EmptySession_ReturnsEmpty()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        var messages = await repo.GetBySessionAsync(sessionId, 10, null);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task CountBySessionAsync_ReturnsCorrectCount()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        await repo.UpsertAsync(MakeMessage(sessionId));
        await repo.UpsertAsync(MakeMessage(sessionId));
        await repo.UpsertAsync(MakeMessage(sessionId));

        var count = await repo.CountBySessionAsync(sessionId);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task DeleteBySessionAsync_RemovesAllMessages()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        await repo.UpsertAsync(MakeMessage(sessionId));
        await repo.UpsertAsync(MakeMessage(sessionId));

        await repo.DeleteBySessionAsync(sessionId);

        var count = await repo.CountBySessionAsync(sessionId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UpsertBatchAsync_InsertsMultipleMessages()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var messages = Enumerable.Range(0, 4)
            .Select(_ => MakeMessage(sessionId))
            .ToList();

        await repo.UpsertBatchAsync(messages);

        var count = await repo.CountBySessionAsync(sessionId);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task CascadeDelete_RemovesMessagesWhenSessionDeleted()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        await repo.UpsertAsync(MakeMessage(sessionId));
        await repo.UpsertAsync(MakeMessage(sessionId));

        // Delete the session — cascade should remove messages
        using var directConn = (SqliteConnection)factory.CreateConnection();
        await directConn.ExecuteAsync("DELETE FROM sessions WHERE id = @Id", new { Id = sessionId });

        var count = await repo.CountBySessionAsync(sessionId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsMessage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var id = Guid.NewGuid().ToString();
        var msg = new PersistedMessage
        {
            Id = id,
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"hello"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        await repo.UpsertAsync(msg);

        var result = await repo.GetByIdAsync(id, sessionId);

        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal("assistant", result.Role);
        Assert.Contains("hello", result.PartsJson);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForMissing()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        var result = await repo.GetByIdAsync("non-existent-id", sessionId);

        Assert.Null(result);
    }
}
