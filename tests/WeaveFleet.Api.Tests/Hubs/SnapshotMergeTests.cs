using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Api.Hubs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Hubs;

/// <summary>
/// Unit tests for snapshot merge logic in SessionEventsHub.
/// Tests the BuildAtomicSnapshotAsync method's behavior when merging
/// persisted messages with in-flight streaming state.
/// </summary>
public sealed class SnapshotMergeTests
{
    [Fact]
    public async Task BuildAtomicSnapshot_EmptySession_ReturnsEmptySnapshot()
    {
        var hub = CreateHub(
            persistedSnapshot: CreateEmptySnapshot("session-1"),
            streamingState: CreateEmptyStreamingState(),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages.ShouldBeEmpty();
        snapshot.ActivityStatus.ShouldBe("idle");
        snapshot.LastEventId.ShouldBeNull();
    }

    [Fact]
    public async Task BuildAtomicSnapshot_PersistedOnly_ReturnsPersistedMessages()
    {
        var persistedMessages = new[]
        {
            CreateMessage("msg-1", "session-1", "Hello", "part-1")
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: CreateEmptyStreamingState(),
            lastEventId: 42);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-1");
        var textPart = snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>();
        textPart.Text.ShouldBe("Hello");
        snapshot.ActivityStatus.ShouldBe("idle");
        snapshot.LastEventId.ShouldBe(42);
    }

    [Fact]
    public async Task BuildAtomicSnapshot_InFlightOnly_ReturnsPersistedWithInFlightDeltas()
    {
        var persistedMessages = new[]
        {
            CreateMessage("msg-1", "session-1", "Hello", "part-1")
        };

        var inFlightDeltas = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["msg-1"] = new Dictionary<string, string>
            {
                ["part-1"] = "Hello World"
            }
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: new StreamingStateSnapshot(null, inFlightDeltas),
            lastEventId: 42);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages.Count.ShouldBe(1);
        var textPart = snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>();
        textPart.Text.ShouldBe("Hello World");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_MergeWithCollisions_InFlightTakesPrecedence()
    {
        var persistedMessages = new[]
        {
            CreateMessage("msg-1", "session-1", "Persisted text", "part-1")
        };

        var inFlightDeltas = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["msg-1"] = new Dictionary<string, string>
            {
                ["part-1"] = "In-flight text"
            }
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: new StreamingStateSnapshot(null, inFlightDeltas),
            lastEventId: 42);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        var textPart = snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>();
        textPart.Text.ShouldBe("In-flight text");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_MultiplePartsInMessage_OnlyUpdatesMatchingParts()
    {
        var persistedMessages = new[]
        {
            CreateMessageWithMultipleParts("msg-1", "session-1",
                ("part-1", "First part"),
                ("part-2", "Second part"))
        };

        var inFlightDeltas = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["msg-1"] = new Dictionary<string, string>
            {
                ["part-1"] = "Updated first part"
            }
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: new StreamingStateSnapshot(null, inFlightDeltas),
            lastEventId: 42);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages[0].Parts.Count.ShouldBe(2);
        var part1 = snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>();
        part1.Text.ShouldBe("Updated first part");
        var part2 = snapshot.Messages[0].Parts[1].ShouldBeOfType<TextMessageEventPart>();
        part2.Text.ShouldBe("Second part");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_ActivityStatus_InFlightTakesPrecedence()
    {
        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", [], "idle"),
            streamingState: new StreamingStateSnapshot(
                new SessionActivitySnapshot("session-1", "busy", "user-1", DateTimeOffset.UtcNow),
                new Dictionary<string, IReadOnlyDictionary<string, string>>()),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.ActivityStatus.ShouldBe("busy");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_ActivityStatus_FallsBackToPersistedWhenNoInFlight()
    {
        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", [], "busy"),
            streamingState: CreateEmptyStreamingState(),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.ActivityStatus.ShouldBe("busy");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_ChronologicalOrdering_PreservesPersistedOrder()
    {
        var persistedMessages = new[]
        {
            CreateMessage("msg-1", "session-1", "First", "part-1"),
            CreateMessage("msg-2", "session-1", "Second", "part-2"),
            CreateMessage("msg-3", "session-1", "Third", "part-3")
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: CreateEmptyStreamingState(),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages.Count.ShouldBe(3);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-1");
        snapshot.Messages[1].Info.Id.ShouldBe("msg-2");
        snapshot.Messages[2].Info.Id.ShouldBe("msg-3");
    }

    [Fact]
    public async Task BuildAtomicSnapshot_NonTextParts_IgnoresInFlightDeltas()
    {
        var persistedMessages = new[]
        {
            CreateMessageWithToolPart("msg-1", "session-1", "tool-part-1", "bash")
        };

        var inFlightDeltas = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["msg-1"] = new Dictionary<string, string>
            {
                ["tool-part-1"] = "Should not apply to tool parts"
            }
        };

        var hub = CreateHub(
            persistedSnapshot: CreateSnapshot("session-1", persistedMessages, "idle"),
            streamingState: new StreamingStateSnapshot(null, inFlightDeltas),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.Messages[0].Parts[0].ShouldBeOfType<ToolMessageEventPart>();
    }

    [Fact]
    public async Task BuildAtomicSnapshot_LastEventIdZero_ReturnsNull()
    {
        var hub = CreateHub(
            persistedSnapshot: CreateEmptySnapshot("session-1"),
            streamingState: CreateEmptyStreamingState(),
            lastEventId: 0);

        var snapshot = await InvokeSnapshotBuilder(hub, "session-1");

        snapshot.LastEventId.ShouldBeNull();
    }

    // ── Test Helpers ────────────────────────────────────────────────────────────────

    private static SessionEventsHub CreateHub(
        SessionSnapshot persistedSnapshot,
        StreamingStateSnapshot streamingState,
        long lastEventId)
    {
        var snapshotBuilder = new FakeSessionSnapshotBuilder(persistedSnapshot);
        
        // Create real StreamingStateProvider with seeded state
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        
        // Seed the activity tracker if streaming state has activity
        if (streamingState.ActivitySnapshot is not null)
        {
            activityTracker.Update(
                streamingState.ActivitySnapshot.FleetSessionId,
                streamingState.ActivitySnapshot.ActivityStatus,
                streamingState.ActivitySnapshot.UserId);
        }
        
        // Seed the delta buffer with buffered deltas
        foreach (var (messageId, parts) in streamingState.BufferedDeltas)
        {
            foreach (var (partId, text) in parts)
            {
                // Extract session ID from the first message in persisted snapshot
                var sessionId = persistedSnapshot.Session.Id;
                deltaBuffer.Append(sessionId, messageId, partId, text);
            }
        }
        
        var streamingStateProvider = new StreamingStateProvider(activityTracker, deltaBuffer);
        var eventStore = new FakeEventStore(lastEventId);
        var broadcaster = new FakeEventBroadcaster();
        var userContext = new TestUserContext("test-user");
        var logger = new FakeLogger<SessionEventsHub>();

        return new SessionEventsHub(
            broadcaster,
            userContext,
            logger,
            snapshotBuilder,
            streamingStateProvider,
            eventStore,
            null!);
    }

    private static async Task<SessionSnapshot> InvokeSnapshotBuilder(SessionEventsHub hub, string sessionId)
    {
        // Use reflection to invoke the private BuildAtomicSnapshotAsync method
        var method = typeof(SessionEventsHub).GetMethod(
            "BuildAtomicSnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.ShouldNotBeNull();

        var task = (Task<SessionSnapshot>)method.Invoke(hub, new object[] { sessionId })!;
        return await task;
    }

    private static SessionSnapshot CreateEmptySnapshot(string sessionId)
    {
        return new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active"
            },
            Messages = [],
            ActivityStatus = "idle",
            LastEventId = null
        };
    }

    private static SessionSnapshot CreateSnapshot(
        string sessionId,
        IReadOnlyList<MessageLifecyclePayload> messages,
        string activityStatus)
    {
        return new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active"
            },
            Messages = messages,
            ActivityStatus = activityStatus,
            LastEventId = null
        };
    }

    private static StreamingStateSnapshot CreateEmptyStreamingState()
    {
        return new StreamingStateSnapshot(
            null,
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    private static MessageLifecyclePayload CreateMessage(
        string messageId,
        string sessionId,
        string text,
        string partId)
    {
        return new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            Parts = new[]
            {
                new TextMessageEventPart
                {
                    Id = partId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    Text = text
                }
            }
        };
    }

    private static MessageLifecyclePayload CreateMessageWithMultipleParts(
        string messageId,
        string sessionId,
        params (string PartId, string Text)[] parts)
    {
        return new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            Parts = parts.Select(p => (MessageEventPart)new TextMessageEventPart
            {
                Id = p.PartId,
                SessionId = sessionId,
                MessageId = messageId,
                Text = p.Text
            }).ToList()
        };
    }

    private static MessageLifecyclePayload CreateMessageWithToolPart(
        string messageId,
        string sessionId,
        string partId,
        string toolName)
    {
        return new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            Parts = new[]
            {
                new ToolMessageEventPart
                {
                    Id = partId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    ToolName = toolName,
                    CallId = "call-1",
                    State = new ToolPendingState
                    {
                        Input = JsonSerializer.SerializeToElement(new { })
                    }
                }
            }
        };
    }

    // ── Fake Implementations ────────────────────────────────────────────────────────

    private sealed class FakeSessionSnapshotBuilder(SessionSnapshot snapshot) : ISessionSnapshotBuilder
    {
        public Task<SessionSnapshot> BuildAsync(string sessionId, int pageSize = 100, string? cursor = null)
            => Task.FromResult(snapshot);
    }

    private sealed class FakeEventStore(long lastEventId) : IEventStore
    {
        public long GetLastEventId(string sessionId) => lastEventId;
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
