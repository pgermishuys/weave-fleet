using NuCode.Events;
using NuCode.Permissions;
using NuCode.Sessions;

namespace NuCode;

public sealed class SessionServiceTests : IDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly NuCodeEventBus _eventBus;
    private readonly SessionService _service;

    public SessionServiceTests()
    {
        _store = new SqliteSessionStore("Data Source=:memory:");
        _eventBus = new NuCodeEventBus();
        _service = new SessionService(_store, _eventBus);
    }

    public void Dispose() => _store.Dispose();

    // ── Session creation ──

    [Fact]
    public async Task CreateSessionReturnsPopulatedSession()
    {
        var session = await _service.CreateSessionAsync("/workspace", "My Session", CancellationToken.None);

        session.Id.ShouldNotBe(default);
        session.Directory.ShouldBe("/workspace");
        session.Title.ShouldBe("My Session");
        session.Slug.ShouldNotBeEmpty();
        session.Version.ShouldBe("0.1.0");
        session.ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task CreateSessionGeneratesDefaultTitle()
    {
        var session = await _service.CreateSessionAsync("/workspace", null, CancellationToken.None);

        session.Title.ShouldStartWith("New session - ");
    }

    [Fact]
    public async Task CreateSessionPublishesCreatedEvent()
    {
        SessionEvents.SessionInfo? captured = null;
        using var sub = _eventBus.Subscribe(SessionEvents.Created, e => captured = e.Properties);

        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.SessionId.ShouldBe(session.Id);
        captured.Title.ShouldBe("Test");
    }

    [Fact]
    public async Task CreateSessionPersistsToStore()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Persisted", CancellationToken.None);

        var retrieved = await _service.GetSessionAsync(session.Id, CancellationToken.None);
        retrieved.ShouldNotBeNull();
        retrieved.Title.ShouldBe("Persisted");
    }

    [Fact]
    public async Task CreateChildSessionLinksToParent()
    {
        var parent = await _service.CreateSessionAsync("/workspace", "Parent", CancellationToken.None);
        var child = await _service.CreateChildSessionAsync(parent.Id, "/workspace", "Child", CancellationToken.None);

        child.ParentId.ShouldBe(parent.Id);
        child.Title.ShouldBe("Child");
    }

    // ── Session mutations ──

    [Fact]
    public async Task SetTitleUpdatesAndPublishesEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Old", CancellationToken.None);

        SessionEvents.SessionInfo? captured = null;
        using var sub = _eventBus.Subscribe(SessionEvents.Updated, e => captured = e.Properties);

        var updated = await _service.SetTitleAsync(session.Id, "New Title", CancellationToken.None);

        updated.Title.ShouldBe("New Title");
        (updated.UpdatedAt > session.UpdatedAt).ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.Title.ShouldBe("New Title");
    }

    [Fact]
    public async Task ArchiveAndUnarchiveSession()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        var archived = await _service.ArchiveSessionAsync(session.Id, CancellationToken.None);
        archived.ArchivedAt.ShouldNotBeNull();

        var unarchived = await _service.UnarchiveSessionAsync(session.Id, CancellationToken.None);
        unarchived.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public async Task SetPermissionsUpdatesSession()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("read", "*", PermissionAction.Allow)],
        };

        var updated = await _service.SetPermissionsAsync(session.Id, ruleset, CancellationToken.None);

        updated.Permissions.ShouldNotBeNull();
        updated.Permissions.Name.ShouldBe("test");
    }

    [Fact]
    public async Task SetAndClearRevert()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var revert = new SessionRevert(MessageId.New());

        var withRevert = await _service.SetRevertAsync(session.Id, revert, CancellationToken.None);
        withRevert.Revert.ShouldNotBeNull();

        var cleared = await _service.ClearRevertAsync(session.Id, CancellationToken.None);
        cleared.Revert.ShouldBeNull();
    }

    [Fact]
    public async Task SetSummaryUpdatesSession()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var summary = new SessionSummary(10, 3, 2);

        var updated = await _service.SetSummaryAsync(session.Id, summary, CancellationToken.None);

        updated.Summary.ShouldNotBeNull();
        updated.Summary.Additions.ShouldBe(10);
    }

    [Fact]
    public async Task TouchSessionUpdatesTimestamp()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        // Small delay to ensure timestamp differs
        await Task.Delay(10);
        var touched = await _service.TouchSessionAsync(session.Id, CancellationToken.None);

        (touched.UpdatedAt >= session.UpdatedAt).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteSessionPublishesDeletedEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Doomed", CancellationToken.None);

        SessionEvents.SessionInfo? captured = null;
        using var sub = _eventBus.Subscribe(SessionEvents.Deleted, e => captured = e.Properties);

        await _service.DeleteSessionAsync(session.Id, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.SessionId.ShouldBe(session.Id);

        var result = await _service.GetSessionAsync(session.Id, CancellationToken.None);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteNonExistentSessionDoesNotPublishEvent()
    {
        SessionEvents.SessionInfo? captured = null;
        using var sub = _eventBus.Subscribe(SessionEvents.Deleted, e => captured = e.Properties);

        await _service.DeleteSessionAsync(SessionId.New(), CancellationToken.None);

        captured.ShouldBeNull();
    }

    [Fact]
    public async Task GetSessionReturnsNullForMissing()
    {
        var result = await _service.GetSessionAsync(SessionId.New(), CancellationToken.None);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListSessionsDelegatesToStore()
    {
        await _service.CreateSessionAsync("/workspace", "A", CancellationToken.None);
        await _service.CreateSessionAsync("/workspace", "B", CancellationToken.None);

        var list = await _service.ListSessionsAsync(new SessionFilter(), CancellationToken.None);
        list.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SetTitleOnMissingSessionThrows()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.SetTitleAsync(SessionId.New(), "nope", CancellationToken.None));
    }

    // ── Messages ──

    [Fact]
    public async Task UpsertMessagePublishesUpdatedEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");

        MessageEvents.MessageInfo? captured = null;
        using var sub = _eventBus.Subscribe(MessageEvents.Updated, e => captured = e.Properties);

        await _service.UpsertMessageAsync(msg, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.SessionId.ShouldBe(session.Id);
        captured.MessageId.ShouldBe(msg.Id);
    }

    [Fact]
    public async Task GetMessagesReturnsPersistedMessages()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _service.UpsertMessageAsync(msg, CancellationToken.None);

        var messages = await _service.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldHaveSingleItem();
        messages[0].Message.Id.ShouldBe(msg.Id);
    }

    [Fact]
    public async Task GetMessagesWithLimitRespectsLimit()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            await _service.UpsertMessageAsync(
                new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build"),
                CancellationToken.None);
        }

        var messages = await _service.GetMessagesAsync(session.Id, 2, CancellationToken.None);
        messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteMessagePublishesRemovedEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _service.UpsertMessageAsync(msg, CancellationToken.None);

        MessageEvents.MessageInfo? captured = null;
        using var sub = _eventBus.Subscribe(MessageEvents.Removed, e => captured = e.Properties);

        await _service.DeleteMessageAsync(session.Id, msg.Id, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MessageId.ShouldBe(msg.Id);

        var messages = await _service.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldBeEmpty();
    }

    // ── Parts ──

    [Fact]
    public async Task UpsertPartPublishesPartUpdatedEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _service.UpsertMessageAsync(msg, CancellationToken.None);

        var part = new TextPart(PartId.New(), session.Id, msg.Id, "Hello");

        MessageEvents.PartInfo? captured = null;
        using var sub = _eventBus.Subscribe(MessageEvents.PartUpdated, e => captured = e.Properties);

        await _service.UpsertPartAsync(part, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.PartId.ShouldBe(part.Id);
        captured.MessageId.ShouldBe(msg.Id);
    }

    [Fact]
    public void PublishPartDeltaFiresDeltaEvent()
    {
        var sessionId = SessionId.New();
        var messageId = MessageId.New();
        var partId = PartId.New();

        MessageEvents.PartDelta? captured = null;
        using var sub = _eventBus.Subscribe(MessageEvents.PartDeltaReceived, e => captured = e.Properties);

        _service.PublishPartDelta(sessionId, messageId, partId, "text", "Hello ");

        captured.ShouldNotBeNull();
        captured.Field.ShouldBe("text");
        captured.Delta.ShouldBe("Hello ");
    }

    [Fact]
    public async Task DeletePartPublishesPartRemovedEvent()
    {
        var session = await _service.CreateSessionAsync("/workspace", "Test", CancellationToken.None);
        var msg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _service.UpsertMessageAsync(msg, CancellationToken.None);

        var part = new TextPart(PartId.New(), session.Id, msg.Id, "Hello");
        await _service.UpsertPartAsync(part, CancellationToken.None);

        MessageEvents.PartInfo? captured = null;
        using var sub = _eventBus.Subscribe(MessageEvents.PartRemoved, e => captured = e.Properties);

        await _service.DeletePartAsync(session.Id, msg.Id, part.Id, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.PartId.ShouldBe(part.Id);
    }

    // ── End-to-end: create session → add messages → retrieve → verify events ──

    [Fact]
    public async Task EndToEndSessionLifecycle()
    {
        var events = new List<NuCodeEvent>();
        using var sub = _eventBus.SubscribeAll(e => events.Add(e));

        // Create session
        var session = await _service.CreateSessionAsync("/workspace", "E2E Test", CancellationToken.None);

        // Add user message with text part
        var userMsg = new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await _service.UpsertMessageAsync(userMsg, CancellationToken.None);
        await _service.UpsertPartAsync(
            new TextPart(PartId.New(), session.Id, userMsg.Id, "Write hello world"),
            CancellationToken.None);

        // Add assistant message with text part
        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id, Agent: "build",
            ProviderId: "anthropic", ModelId: "claude-sonnet-4-20250514");
        await _service.UpsertMessageAsync(assistantMsg, CancellationToken.None);
        await _service.UpsertPartAsync(
            new TextPart(PartId.New(), session.Id, assistantMsg.Id, "Here is your code"),
            CancellationToken.None);

        // Retrieve and verify
        var messages = await _service.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(2);

        var user = messages.First(m => m.Message.Role == MessageRole.User);
        user.Parts.ShouldHaveSingleItem();
        user.Parts[0].ShouldBeOfType<TextPart>();

        var assistant = messages.First(m => m.Message.Role == MessageRole.Assistant);
        assistant.Parts.ShouldHaveSingleItem();

        // Verify events were published (session.created, 2x message.updated, 2x part.updated)
        (events.Count >= 5).ShouldBeTrue($"Expected at least 5 events, got {events.Count}");
        events.ShouldContain(e => e.Type == "session.created");
        events.Count(e => e.Type == "message.updated").ShouldBe(2);
        events.Count(e => e.Type == "message.part.updated").ShouldBe(2);
    }
}
