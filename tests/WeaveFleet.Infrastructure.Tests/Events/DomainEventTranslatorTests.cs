using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;

namespace WeaveFleet.Infrastructure.Tests.Events;

public sealed class DomainEventTranslatorTests
{
    [Fact]
    public void Should_translate_message_created()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg-1",
                    role = "assistant",
                    sessionID = "harness-1",
                    agent = "loom",
                    modelID = "anthropic/claude-sonnet-4-5",
                    parentID = "msg-0",
                    time = new { created = 1_700_000_000_000L, completed = 1_700_000_000_500L },
                    cost = 0.42,
                    tokens = new { input = 10, output = 20, reasoning = 5 }
                },
                parts = new object[]
                {
                    new
                    {
                        type = "text",
                        id = "part-1",
                        sessionID = "harness-1",
                        messageID = "msg-1",
                        text = "Hello"
                    }
                }
            })
        });

        var created = result.ShouldBeOfType<MessageCreated>();
        created.Payload.Info.SessionId.ShouldBe("fleet-1");
        created.Payload.Info.Id.ShouldBe("msg-1");
        created.Payload.Parts.Count.ShouldBe(1);
        created.Payload.Parts[0].SessionId.ShouldBe("fleet-1");
    }

    [Fact]
    public void Should_translate_message_updated()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg-2",
                    role = "assistant",
                    sessionID = "harness-1",
                    time = new { created = 1_700_000_001_000L }
                },
                parts = new object[]
                {
                    new
                    {
                        type = "reasoning",
                        id = "part-2",
                        sessionID = "harness-1",
                        messageID = "msg-2",
                        text = "Thinking",
                        summary = "Plan"
                    }
                }
            })
        });

        var updated = result.ShouldBeOfType<MessageUpdated>();
        updated.Payload.Info.SessionId.ShouldBe("fleet-1");
        updated.Payload.Parts[0].ShouldBeOfType<ReasoningMessageEventPart>().Summary.ShouldBe("Plan");
    }

    [Fact]
    public void Should_translate_message_part_updated()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessagePartUpdated,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = "harness-1",
                part = new
                {
                    type = "step-finish",
                    id = "part-3",
                    sessionID = "harness-1",
                    messageID = "msg-3",
                    index = 2,
                    reason = "completed",
                    cost = 1.5,
                    tokens = new { input = 4, output = 8, reasoning = 2 },
                    completedAt = 1_700_000_002_000L
                }
            })
        });

        var updated = result.ShouldBeOfType<MessagePartUpdated>();
        updated.Payload.SessionId.ShouldBe("fleet-1");
        var part = updated.Payload.Part.ShouldBeOfType<StepFinishedMessageEventPart>();
        part.SessionId.ShouldBe("fleet-1");
        part.Index.ShouldBe(2);
    }

    [Fact]
    public void Should_translate_message_part_delta()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = "harness-1",
                messageID = "msg-4",
                partID = "part-4",
                field = "text",
                delta = " world"
            })
        });

        var streamed = result.ShouldBeOfType<MessagePartDeltaStreamed>();
        streamed.Payload.SessionId.ShouldBe("fleet-1");
        streamed.Payload.MessageId.ShouldBe("msg-4");
        streamed.Payload.PartId.ShouldBe("part-4");
    }

    [Fact]
    public void Should_translate_session_created_to_session_started()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionCreated,
            SessionId = "harness-child",
            FleetSessionId = "fleet-child",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = "harness-child",
                info = new
                {
                    parentID = "fleet-parent",
                    title = "Child session",
                    projectID = "project-1",
                    directory = "/workspace/child"
                }
            })
        });

        var started = result.ShouldBeOfType<SessionStarted>();
        started.Payload.SessionId.ShouldBe("fleet-child");
        started.Payload.ParentSessionId.ShouldBe("fleet-parent");
        started.Payload.Title.ShouldBe("Child session");
        started.Payload.WorkspaceId.ShouldBe("/workspace/child");
    }

    [Fact]
    public void Should_translate_session_deleted()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionDeleted,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        var deleted = result.ShouldBeOfType<SessionDeleted>();
        deleted.Payload.SessionId.ShouldBe("fleet-1");
    }

    [Fact]
    public void Should_translate_delegation_created()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateDelegationEvent(
            "delegation.created",
            new
            {
                delegationId = "delegation-1",
                parentSessionId = "fleet-parent",
                parentToolCallId = "call-1",
                childSessionId = (string?)null,
                title = "Review pull request",
                status = "pending",
                createdAt = "2026-05-16T10:11:12.0000000Z"
            }));

        var created = result.ShouldBeOfType<DelegationCreated>();
        created.Payload.DelegationId.ShouldBe("delegation-1");
        created.Payload.ParentSessionId.ShouldBe("fleet-parent");
    }

    [Fact]
    public void Should_translate_delegation_updated()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateDelegationEvent(
            "delegation.updated",
            new
            {
                delegationId = "delegation-1",
                parentSessionId = "fleet-parent",
                parentToolCallId = "call-1",
                childSessionId = "fleet-child",
                title = "Review pull request",
                status = "running",
                createdAt = "2026-05-16T10:11:12.0000000Z"
            }));

        var updated = result.ShouldBeOfType<DelegationUpdated>();
        updated.Payload.ChildSessionId.ShouldBe("fleet-child");
        updated.Payload.Status.ShouldBe("running");
    }

    [Fact]
    public void Should_translate_delegation_completed()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateDelegationEvent(
            "delegation.completed",
            new
            {
                delegationId = "delegation-1",
                parentSessionId = "fleet-parent",
                parentToolCallId = "call-1",
                childSessionId = "fleet-child",
                title = "Review pull request",
                status = "completed",
                createdAt = "2026-05-16T10:11:12.0000000Z",
                completedAt = "2026-05-16T10:15:12.0000000Z"
            }));

        var completed = result.ShouldBeOfType<DelegationCompleted>();
        completed.Payload.CompletedAt.ShouldBe("2026-05-16T10:15:12.0000000Z");
    }

    [Theory]
    [InlineData(EventTypes.MessageRemoved)]
    [InlineData(EventTypes.MessagePartRemoved)]
    [InlineData(EventTypes.SessionUpdated)]
    [InlineData(EventTypes.SessionError)]
    [InlineData(EventTypes.SessionCompacted)]
    [InlineData(EventTypes.SessionDiff)]
    [InlineData(EventTypes.Error)]
    [InlineData(EventTypes.ServerHeartbeat)]
    [InlineData(EventTypes.ServerConnected)]
    [InlineData("permission.request")]
    public void Should_drop_known_non_domain_events(string eventType)
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = eventType,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        result.ShouldBeNull();
    }

    [Fact]
    public void Should_log_warning_and_drop_unknown_event_type()
    {
        var logger = new ListLogger<DomainEventTranslator>();
        var translator = new DomainEventTranslator(logger);

        var result = translator.Translate(new HarnessEvent
        {
            Type = "future.event",
            SessionId = "harness-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        result.ShouldBeNull();
        logger.Entries.ShouldContain(entry =>
            entry.LogLevel == LogLevel.Warning
            && entry.Message.Contains("future.event", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_emit_turn_started_when_transitioning_from_idle_to_busy()
    {
        var translator = CreateTranslator();

        var first = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = new
                {
                    type = "busy",
                    messageID = "msg-10",
                    index = 0,
                    agent = "loom",
                    modelID = "anthropic/claude-sonnet-4-5",
                    parentID = "msg-9"
                }
            })
        });

        var second = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "working" } })
        });

        var started = first.ShouldBeOfType<TurnStarted>();
        started.Payload.SessionId.ShouldBe("fleet-1");
        started.Payload.MessageId.ShouldBe("msg-10");
        started.Payload.Index.ShouldBe(0);
        second.ShouldBeNull();
    }

    [Fact]
    public void Should_emit_turn_ended_when_session_idle_signal_arrives()
    {
        var translator = CreateTranslator();

        _ = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg-11",
                    role = "assistant",
                    sessionID = "harness-1",
                    agent = "loom",
                    modelID = "anthropic/claude-sonnet-4-5",
                    time = new { created = 1_700_000_010_000L, completed = 1_700_000_010_999L },
                    cost = 0.7,
                    tokens = new { input = 12, output = 24, reasoning = 6 }
                }
            })
        });

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        var ended = result.ShouldBeOfType<TurnEnded>();
        ended.Payload.SessionId.ShouldBe("fleet-1");
        ended.Payload.MessageId.ShouldBe("msg-11");
        ended.Payload.Cost.ShouldBe(0.7);
        ended.Payload.Tokens.ShouldNotBeNull();
        ended.Payload.Tokens.Output.ShouldBe(24);
        ended.Payload.CompletedAt.ShouldBe(1_700_000_010_999L);
    }

    [Fact]
    public void Should_emit_turn_ended_when_session_status_idle_arrives()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = new
                {
                    type = "idle",
                    messageID = "msg-12",
                    index = 3,
                    reason = "completed",
                    cost = 1.2,
                    completedAt = 1_700_000_020_000L,
                    tokens = new { input = 5, output = 10, reasoning = 1 }
                }
            })
        });

        var ended = result.ShouldBeOfType<TurnEnded>();
        ended.Payload.MessageId.ShouldBe("msg-12");
        ended.Payload.Index.ShouldBe(3);
        ended.Payload.Reason.ShouldBe("completed");
        ended.Payload.Cost.ShouldBe(1.2);
        ended.Payload.Tokens.ShouldNotBeNull();
        ended.Payload.Tokens.Input.ShouldBe(5);
    }

    private static DomainEventTranslator CreateTranslator()
        => new(NullLogger<DomainEventTranslator>.Instance);

    private static HarnessEvent CreateDelegationEvent(string eventType, object payload)
        => new()
        {
            Type = eventType,
            SessionId = "harness-parent",
            FleetSessionId = "fleet-parent",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, EventId EventId, string Message);
}
