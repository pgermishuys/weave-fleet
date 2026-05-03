using System.Collections.Immutable;
using NuCode.Sessions;

namespace NuCode;

public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        // In-memory SQLite database for each test
        _store = new SqliteSessionStore("Data Source=:memory:");
    }

    public void Dispose() => _store.Dispose();

    private static NuCodeSession CreateSession(SessionId? id = null, string? parentId = null) => new()
    {
        Id = id ?? SessionId.New(),
        Slug = "test-session",
        Directory = "/workspace",
        Title = "Test Session",
        Version = "1.0.0",
        ParentId = parentId is not null ? new SessionId(parentId) : null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ── Session CRUD ──

    [Fact]
    public async Task CreateAndGetSession()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var retrieved = await _store.GetSessionAsync(session.Id, CancellationToken.None);

        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe(session.Id);
        retrieved.Slug.ShouldBe(session.Slug);
        retrieved.Title.ShouldBe(session.Title);
        retrieved.Directory.ShouldBe(session.Directory);
        retrieved.Version.ShouldBe(session.Version);
    }

    [Fact]
    public async Task GetSessionReturnsNullForMissing()
    {
        var result = await _store.GetSessionAsync(SessionId.New(), CancellationToken.None);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateSessionPersistsChanges()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var updated = session with { Title = "Updated Title", UpdatedAt = DateTimeOffset.UtcNow };
        await _store.UpdateSessionAsync(updated, CancellationToken.None);

        var retrieved = await _store.GetSessionAsync(session.Id, CancellationToken.None);
        retrieved.ShouldNotBeNull();
        retrieved.Title.ShouldBe("Updated Title");
    }

    [Fact]
    public async Task DeleteSessionRemovesIt()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);
        await _store.DeleteSessionAsync(session.Id, CancellationToken.None);

        var result = await _store.GetSessionAsync(session.Id, CancellationToken.None);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListSessionsOrderedByUpdatedDesc()
    {
        var older = CreateSession() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
        var newer = CreateSession() with { UpdatedAt = DateTimeOffset.UtcNow };

        await _store.CreateSessionAsync(older, CancellationToken.None);
        await _store.CreateSessionAsync(newer, CancellationToken.None);

        var list = await _store.ListSessionsAsync(new SessionFilter(), CancellationToken.None);

        list.Count.ShouldBe(2);
        (list[0].UpdatedAt >= list[1].UpdatedAt).ShouldBeTrue();
    }

    [Fact]
    public async Task ListSessionsWithDirectoryFilter()
    {
        var s1 = CreateSession() with { Directory = "/workspace/a" };
        var s2 = CreateSession() with { Directory = "/workspace/b" };

        await _store.CreateSessionAsync(s1, CancellationToken.None);
        await _store.CreateSessionAsync(s2, CancellationToken.None);

        var list = await _store.ListSessionsAsync(new SessionFilter { Directory = "/workspace/a" }, CancellationToken.None);

        list.ShouldHaveSingleItem();
        list[0].Directory.ShouldBe("/workspace/a");
    }

    [Fact]
    public async Task ListSessionsRootsOnlyExcludesChildren()
    {
        var parent = CreateSession();
        await _store.CreateSessionAsync(parent, CancellationToken.None);

        var child = CreateSession() with { ParentId = parent.Id };
        await _store.CreateSessionAsync(child, CancellationToken.None);

        var list = await _store.ListSessionsAsync(new SessionFilter { RootsOnly = true }, CancellationToken.None);

        list.ShouldHaveSingleItem();
        list[0].ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task ListSessionsWithLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.CreateSessionAsync(CreateSession(), CancellationToken.None);
        }

        var list = await _store.ListSessionsAsync(new SessionFilter { Limit = 2 }, CancellationToken.None);
        list.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListSessionsExcludeArchived()
    {
        var active = CreateSession();
        var archived = CreateSession() with { ArchivedAt = DateTimeOffset.UtcNow };

        await _store.CreateSessionAsync(active, CancellationToken.None);
        await _store.CreateSessionAsync(archived, CancellationToken.None);

        var list = await _store.ListSessionsAsync(new SessionFilter { ExcludeArchived = true }, CancellationToken.None);

        list.ShouldHaveSingleItem();
        list[0].ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListSessionsSearchByTitle()
    {
        var s1 = CreateSession() with { Title = "Fix auth bug" };
        var s2 = CreateSession() with { Title = "Add feature" };

        await _store.CreateSessionAsync(s1, CancellationToken.None);
        await _store.CreateSessionAsync(s2, CancellationToken.None);

        var list = await _store.ListSessionsAsync(new SessionFilter { Search = "auth" }, CancellationToken.None);

        list.ShouldHaveSingleItem();
        list[0].Title.ShouldContain("auth");
    }

    [Fact]
    public async Task GetChildSessions()
    {
        var parent = CreateSession();
        await _store.CreateSessionAsync(parent, CancellationToken.None);

        var child1 = CreateSession() with { ParentId = parent.Id };
        var child2 = CreateSession() with { ParentId = parent.Id };
        await _store.CreateSessionAsync(child1, CancellationToken.None);
        await _store.CreateSessionAsync(child2, CancellationToken.None);

        var children = await _store.GetChildSessionsAsync(parent.Id, CancellationToken.None);
        children.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SessionWithSummaryPersists()
    {
        var session = CreateSession() with
        {
            Summary = new SessionSummary(Additions: 10, Deletions: 3, Files: 2),
        };

        await _store.CreateSessionAsync(session, CancellationToken.None);
        var retrieved = await _store.GetSessionAsync(session.Id, CancellationToken.None);

        retrieved.ShouldNotBeNull();
        retrieved.Summary.ShouldNotBeNull();
        retrieved.Summary.Additions.ShouldBe(10);
        retrieved.Summary.Deletions.ShouldBe(3);
        retrieved.Summary.Files.ShouldBe(2);
    }

    [Fact]
    public async Task SessionWithArchiveTimePersists()
    {
        var archivedAt = DateTimeOffset.UtcNow;
        var session = CreateSession() with { ArchivedAt = archivedAt };

        await _store.CreateSessionAsync(session, CancellationToken.None);
        var retrieved = await _store.GetSessionAsync(session.Id, CancellationToken.None);

        retrieved.ShouldNotBeNull();
        retrieved.ArchivedAt.ShouldNotBeNull();
        // Compare to millisecond precision (SQLite stores ms)
        retrieved.ArchivedAt.Value.ToUnixTimeMilliseconds().ShouldBe(archivedAt.ToUnixTimeMilliseconds());
    }

    // ── Message CRUD ──

    [Fact]
    public async Task UpsertAndGetMessages()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var userMsg = new UserMessage(
            Id: MessageId.New(),
            SessionId: session.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            Agent: "build");

        await _store.UpsertMessageAsync(userMsg, CancellationToken.None);

        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldHaveSingleItem();
        messages[0].Message.Id.ShouldBe(userMsg.Id);
        messages[0].Message.Role.ShouldBe(MessageRole.User);
    }

    [Fact]
    public async Task UpsertMessageUpdatesOnConflict()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msgId = MessageId.New();
        var original = new UserMessage(msgId, session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(original, CancellationToken.None);

        var updated = new UserMessage(msgId, session.Id, DateTimeOffset.UtcNow, "plan");
        await _store.UpsertMessageAsync(updated, CancellationToken.None);

        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldHaveSingleItem();
        var retrieved = messages[0].Message.ShouldBeOfType<UserMessage>();
        retrieved.Agent.ShouldBe("plan");
    }

    [Fact]
    public async Task AssistantMessageRoundTrips()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var parentMsgId = MessageId.New();
        var userMsg = new UserMessage(parentMsgId, session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(userMsg, CancellationToken.None);

        var assistantMsg = new AssistantMessage(
            Id: MessageId.New(),
            SessionId: session.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            ParentId: parentMsgId,
            Agent: "build",
            ProviderId: "anthropic",
            ModelId: "claude-sonnet-4-20250514",
            Cost: 0.015m,
            Tokens: new TokenUsage(1000, 500, 200, new CacheTokenUsage(100, 50), Total: 1700));

        await _store.UpsertMessageAsync(assistantMsg, CancellationToken.None);

        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(2);

        var retrieved = messages.Select(m => m.Message).OfType<AssistantMessage>().Single();
        retrieved.ParentId.ShouldBe(parentMsgId);
        retrieved.Cost.ShouldBe(0.015m);
        retrieved.Tokens.ShouldNotBeNull();
        retrieved.Tokens.Total.ShouldBe(1700);
    }

    [Fact]
    public async Task DeleteMessageRemovesIt()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);
        await _store.DeleteMessageAsync(session.Id, msg.Id, CancellationToken.None);

        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMessagesWithLimit()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await _store.UpsertMessageAsync(new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build"), CancellationToken.None);
        }

        var messages = await _store.GetMessagesAsync(session.Id, 2, CancellationToken.None);
        messages.Count.ShouldBe(2);
    }

    // ── Part CRUD ──

    [Fact]
    public async Task UpsertAndGetParts()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);

        var textPart = new TextPart(PartId.New(), session.Id, msg.Id, "Hello world");
        await _store.UpsertPartAsync(textPart, CancellationToken.None);

        var parts = await _store.GetPartsAsync(msg.Id, CancellationToken.None);
        parts.ShouldHaveSingleItem();
        var retrieved = parts[0].ShouldBeOfType<TextPart>();
        retrieved.Text.ShouldBe("Hello world");
    }

    [Fact]
    public async Task ToolPartWithStateRoundTrips()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);

        var input = ImmutableDictionary<string, object?>.Empty.Add("command", "ls");
        var toolPart = new ToolPart(
            PartId.New(), session.Id, msg.Id, "call_123", "bash",
            new CompletedToolCallState(
                input,
                Output: "file1.txt\nfile2.txt",
                Title: "Ran ls",
                Metadata: ImmutableDictionary<string, object?>.Empty,
                StartTime: DateTimeOffset.UtcNow.AddSeconds(-1),
                EndTime: DateTimeOffset.UtcNow));

        await _store.UpsertPartAsync(toolPart, CancellationToken.None);

        var parts = await _store.GetPartsAsync(msg.Id, CancellationToken.None);
        parts.ShouldHaveSingleItem();
        var retrieved = parts[0].ShouldBeOfType<ToolPart>();
        retrieved.ToolName.ShouldBe("bash");
        retrieved.State.Status.ShouldBe(ToolCallStatus.Completed);

        var state = retrieved.State.ShouldBeOfType<CompletedToolCallState>();
        state.Output.ShouldBe("file1.txt\nfile2.txt");
    }

    [Fact]
    public async Task DeletePartRemovesIt()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);

        var part = new TextPart(PartId.New(), session.Id, msg.Id, "Hello");
        await _store.UpsertPartAsync(part, CancellationToken.None);
        await _store.DeletePartAsync(session.Id, msg.Id, part.Id, CancellationToken.None);

        var parts = await _store.GetPartsAsync(msg.Id, CancellationToken.None);
        parts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMessagesIncludesPartsGroupedByMessage()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg1 = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        var msg2 = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg1, CancellationToken.None);
        await _store.UpsertMessageAsync(msg2, CancellationToken.None);

        await _store.UpsertPartAsync(new TextPart(PartId.New(), session.Id, msg1.Id, "Part for msg1"), CancellationToken.None);
        await _store.UpsertPartAsync(new TextPart(PartId.New(), session.Id, msg2.Id, "Part A for msg2"), CancellationToken.None);
        await _store.UpsertPartAsync(new TextPart(PartId.New(), session.Id, msg2.Id, "Part B for msg2"), CancellationToken.None);

        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(2);

        var msg1WithParts = messages.First(m => m.Message.Id == msg1.Id);
        var msg2WithParts = messages.First(m => m.Message.Id == msg2.Id);
        msg1WithParts.Parts.ShouldHaveSingleItem();
        msg2WithParts.Parts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CascadeDeleteRemovesMessagesAndParts()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);
        await _store.UpsertPartAsync(new TextPart(PartId.New(), session.Id, msg.Id, "Hello"), CancellationToken.None);

        await _store.DeleteSessionAsync(session.Id, CancellationToken.None);

        // Messages and parts should be gone
        var messages = await _store.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldBeEmpty();

        var parts = await _store.GetPartsAsync(msg.Id, CancellationToken.None);
        parts.ShouldBeEmpty();
    }

    [Fact]
    public async Task MultiplePartTypesRoundTrip()
    {
        var session = CreateSession();
        await _store.CreateSessionAsync(session, CancellationToken.None);

        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _store.UpsertMessageAsync(msg, CancellationToken.None);

        var parts = new MessagePart[]
        {
            new TextPart(PartId.New(), session.Id, msg.Id, "Hello"),
            new ReasoningPart(PartId.New(), session.Id, msg.Id, "Thinking...", DateTimeOffset.UtcNow),
            new FilePart(PartId.New(), session.Id, msg.Id, "image/png", "data:image/png;base64,...", "test.png"),
            new SnapshotPart(PartId.New(), session.Id, msg.Id, "snap123"),
            new PatchPart(PartId.New(), session.Id, msg.Id, "hash1", ImmutableArray.Create("file.cs")),
            new AgentPart(PartId.New(), session.Id, msg.Id, "build"),
            new CompactionPart(PartId.New(), session.Id, msg.Id, Auto: true),
            new SubtaskPart(PartId.New(), session.Id, msg.Id, "prompt", "desc", "explore"),
            new StepStartPart(PartId.New(), session.Id, msg.Id, Snapshot: "s1"),
            new StepFinishPart(PartId.New(), session.Id, msg.Id, "stop", 0.01m,
                new TokenUsage(1000, 500, 0, new CacheTokenUsage(0, 0))),
        };

        foreach (var part in parts)
        {
            await _store.UpsertPartAsync(part, CancellationToken.None);
        }

        var retrieved = await _store.GetPartsAsync(msg.Id, CancellationToken.None);
        retrieved.Count.ShouldBe(10);

        // Verify each type survived round-trip
        retrieved.First(p => p.Type == "text").ShouldBeOfType<TextPart>();
        retrieved.First(p => p.Type == "reasoning").ShouldBeOfType<ReasoningPart>();
        retrieved.First(p => p.Type == "file").ShouldBeOfType<FilePart>();
        retrieved.First(p => p.Type == "snapshot").ShouldBeOfType<SnapshotPart>();
        retrieved.First(p => p.Type == "patch").ShouldBeOfType<PatchPart>();
        retrieved.First(p => p.Type == "agent").ShouldBeOfType<AgentPart>();
        retrieved.First(p => p.Type == "compaction").ShouldBeOfType<CompactionPart>();
        retrieved.First(p => p.Type == "subtask").ShouldBeOfType<SubtaskPart>();
        retrieved.First(p => p.Type == "step-start").ShouldBeOfType<StepStartPart>();
        retrieved.First(p => p.Type == "step-finish").ShouldBeOfType<StepFinishPart>();
    }
}
