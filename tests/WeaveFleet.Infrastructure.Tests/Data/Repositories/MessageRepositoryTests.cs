using Dapper;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class MessageRepositoryTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<(SqliteConnection Keeper, MessageRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new MessageRepository(factory, new TestUserContext());
        return (keeper, repo, factory);
    }

    /// <summary>
    /// Inserts a session row so foreign-key constraints on messages are satisfied.
    /// </summary>
    private static async Task<string> SeedSessionAsync(IDbConnectionFactory factory, string? sessionId = null, string userId = TestUserContext.DefaultUserId)
        => (await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId, sessionId: sessionId)).Session.Id;

    private static PersistedMessage MakeMessage(string sessionId, string? id = null, string? timestamp = null, string? createdAt = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"hello"}]""",
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToString("O"),
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
        count.ShouldBe(1);
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
        messages.Count.ShouldBe(1);
        messages[0].PartsJson.ShouldContain("complete");
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsMessagesInAscendingCreatedAtOrderWithinPage()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var baseTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseCreatedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-1",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"first"}]""",
            Timestamp = baseTimestamp.AddMinutes(3).ToString("O"),
            CreatedAt = baseCreatedAt.ToString("O"),
        });

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-2",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"second"}]""",
            Timestamp = baseTimestamp.AddMinutes(1).ToString("O"),
            CreatedAt = baseCreatedAt.AddMinutes(1).ToString("O"),
        });

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-3",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"third"}]""",
            Timestamp = baseTimestamp.AddMinutes(2).ToString("O"),
            CreatedAt = baseCreatedAt.AddMinutes(2).ToString("O"),
        });

        var messages = await repo.GetBySessionAsync(sessionId, 10, null);

        messages.Select(message => message.Id).ShouldBe(["msg-1", "msg-2", "msg-3"]);
        string.Compare(messages[0].CreatedAt, messages[1].CreatedAt, StringComparison.Ordinal).ShouldBeLessThanOrEqualTo(0);
        string.Compare(messages[1].CreatedAt, messages[2].CreatedAt, StringComparison.Ordinal).ShouldBeLessThanOrEqualTo(0);
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

        page.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetBySessionAsync_WithCursor_ReturnsOlderMessages()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var baseTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseCreatedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString()).ToList();

        // Insert in ascending durable order (c0..c4)
        for (var i = 0; i < 5; i++)
        {
            await repo.UpsertAsync(MakeMessage(
                sessionId,
                id: ids[i],
                timestamp: baseTimestamp.AddMinutes(10 - i).ToString("O"),
                createdAt: baseCreatedAt.AddMinutes(i).ToString("O")));
        }

        // Request page before the 3rd persisted message (ids[2] = c2) — should get c0 and c1
        var page = await repo.GetBySessionAsync(sessionId, 10, ids[2]);

        page.Select(message => message.Id).ShouldBe([ids[0], ids[1]]);
    }

    [Fact]
    public async Task GetBySessionAsync_NoCursor_UsesCreatedAtForTailSelection()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var baseTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseCreatedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-earlier-write-newer-logical-time",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"older write"}]""",
            Timestamp = baseTimestamp.AddMinutes(10).ToString("O"),
            CreatedAt = baseCreatedAt.ToString("O"),
        });

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-later-write-older-logical-time",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"newer write"}]""",
            Timestamp = baseTimestamp.ToString("O"),
            CreatedAt = baseCreatedAt.AddMinutes(1).ToString("O"),
        });

        var page = await repo.GetBySessionAsync(sessionId, 1, null);

        page.Count.ShouldBe(1);
        page[0].Id.ShouldBe("msg-later-write-older-logical-time");
    }

    [Fact]
    public async Task GetBySessionAsync_WithCursor_UsesCreatedAtForOlderPages()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        var baseTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseCreatedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-1",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"first"}]""",
            Timestamp = baseTimestamp.AddMinutes(2).ToString("O"),
            CreatedAt = baseCreatedAt.ToString("O"),
        });

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-2",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"second"}]""",
            Timestamp = baseTimestamp.AddMinutes(1).ToString("O"),
            CreatedAt = baseCreatedAt.AddMinutes(1).ToString("O"),
        });

        await repo.UpsertAsync(new PersistedMessage
        {
            Id = "msg-3",
            SessionId = sessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"third"}]""",
            Timestamp = baseTimestamp.ToString("O"),
            CreatedAt = baseCreatedAt.AddMinutes(2).ToString("O"),
        });

        var page = await repo.GetBySessionAsync(sessionId, 10, "msg-3");

        page.Select(message => message.Id).ShouldBe(["msg-1", "msg-2"]);
    }

    [Fact]
    public async Task GetBySessionAsync_WithCursor_UsesIdAsTieBreakerWhenCreatedAtMatches()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);
        const string createdAt = "2026-01-01T01:00:00.0000000+00:00";

        await repo.UpsertAsync(MakeMessage(sessionId, id: "msg-a", timestamp: "2026-01-01T00:02:00.0000000+00:00", createdAt: createdAt));
        await repo.UpsertAsync(MakeMessage(sessionId, id: "msg-b", timestamp: "2026-01-01T00:01:00.0000000+00:00", createdAt: createdAt));
        await repo.UpsertAsync(MakeMessage(sessionId, id: "msg-c", timestamp: "2026-01-01T00:00:00.0000000+00:00", createdAt: createdAt));

        var page = await repo.GetBySessionAsync(sessionId, 10, "msg-c");

        page.Select(message => message.Id).ShouldBe(["msg-a", "msg-b"]);
    }

    [Fact]
    public async Task GetBySessionAsync_EmptySession_ReturnsEmpty()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        var messages = await repo.GetBySessionAsync(sessionId, 10, null);

        messages.ShouldBeEmpty();
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

        count.ShouldBe(3);
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
        count.ShouldBe(0);
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
        count.ShouldBe(4);
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
        count.ShouldBe(0);
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

        result.ShouldNotBeNull();
        result.Id.ShouldBe(id);
        result.SessionId.ShouldBe(sessionId);
        result.Role.ShouldBe("assistant");
        result.PartsJson.ShouldContain("hello");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForMissing()
    {
        var (conn, repo, factory) = await CreateAsync();
        using var _ = conn;

        var sessionId = await SeedSessionAsync(factory);

        var result = await repo.GetByIdAsync("non-existent-id", sessionId);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetBySessionAsync_DoesNotReturnOtherUsersMessages()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var otherSessionId = await SeedSessionAsync(factory, userId: "other-user");
        var otherRepo = new MessageRepository(factory, new TestUserContext("other-user"));
        await otherRepo.UpsertAsync(MakeMessage(otherSessionId));

        var repo = new MessageRepository(factory, new TestUserContext());
        var messages = await repo.GetBySessionAsync(otherSessionId, 10, null);

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_DoesNotWriteToOtherUsersSession()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var otherSessionId = await SeedSessionAsync(factory, userId: "other-user");
        var countBefore = await new MessageRepository(factory, new TestUserContext("other-user")).CountBySessionAsync(otherSessionId);
        var repo = new MessageRepository(factory, new TestUserContext());

        await repo.UpsertAsync(MakeMessage(otherSessionId));

        var otherRepo = new MessageRepository(factory, new TestUserContext("other-user"));
        var count = await otherRepo.CountBySessionAsync(otherSessionId);
        count.ShouldBe(countBefore);
    }

    [Fact]
    public async Task DeleteBySessionAsync_DoesNotDeleteOtherUsersMessages()
    {
        var (conn, _, factory) = await CreateAsync();
        using var _ = conn;

        var otherSessionId = await SeedSessionAsync(factory, userId: "other-user");
        var otherRepo = new MessageRepository(factory, new TestUserContext("other-user"));
        await otherRepo.UpsertAsync(MakeMessage(otherSessionId));

        var repo = new MessageRepository(factory, new TestUserContext());
        await repo.DeleteBySessionAsync(otherSessionId);

        var count = await otherRepo.CountBySessionAsync(otherSessionId);
        count.ShouldBe(1);
    }
}
