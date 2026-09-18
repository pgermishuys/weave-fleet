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
    public void Should_translate_an_opencode_file_watcher_event_to_files_changed()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.FileWatcherUpdated,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { file = "/repo/src/app.ts", @event = "add" }),
        });

        var changed = result.ShouldBeOfType<FilesChanged>();
        changed.Payload.SessionId.ShouldBe("fleet-1");
        changed.Payload.Files.ShouldHaveSingleItem().Path.ShouldBe("/repo/src/app.ts");
        changed.Payload.Files[0].ChangeType.ShouldBe("add");
    }

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
    public void Should_emit_session_idled_when_session_idle_signal_arrives()
    {
        var translator = CreateTranslator();

        _ = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        });

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        var idled = result.ShouldBeOfType<SessionIdled>();
        idled.Payload.SessionId.ShouldBe("fleet-1");
    }

    [Fact]
    public void Should_emit_session_idled_when_session_status_idle_arrives()
    {
        var translator = CreateTranslator();

        _ = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        });

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

        var idled = result.ShouldBeOfType<SessionIdled>();
        idled.Payload.SessionId.ShouldBe("fleet-1");
    }

    [Fact]
    public void should_emit_exactly_one_session_idled_when_both_idle_signals_arrive()
    {
        var translator = CreateTranslator();

        var started = translator.Translate(new HarnessEvent
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
                    messageID = "msg-no-tool",
                    index = 4
                }
            })
        });

        var statusIdle = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "idle" } })
        });

        var sessionIdle = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        started.ShouldBeOfType<TurnStarted>();
        var idledEvents = new[] { statusIdle, sessionIdle }
            .OfType<SessionIdled>()
            .ToArray();

        idledEvents.Length.ShouldBe(1);
        idledEvents[0].Payload.SessionId.ShouldBe("fleet-1");
    }

    [Fact]
    public void should_not_emit_session_idled_for_idle_signal_when_already_idle()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        result.ShouldBeNull();
    }

    [Fact]
    public void Should_translate_todos_reported_for_the_routed_fleet_session()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateTodosEvent(new
        {
            items = new object[]
            {
                new { content = "Write the migration", status = "completed", priority = "high" },
                new { content = "Drop the indexes", status = "in_progress" },
                new { content = "Start the app", status = "pending", priority = "low" },
            }
        }));

        var todos = result.ShouldBeOfType<TodosReported>();
        todos.Payload.SessionId.ShouldBe("fleet-1");
        todos.Payload.Items.Select(item => (item.Content, item.Status, item.Priority)).ShouldBe(
        [
            ("Write the migration", TodoStatuses.Completed, "high"),
            ("Drop the indexes", TodoStatuses.InProgress, null),
            ("Start the app", TodoStatuses.Pending, "low"),
        ]);
    }

    [Fact]
    public void Should_translate_an_empty_todo_list()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateTodosEvent(new { items = Array.Empty<object>() }));

        result.ShouldBeOfType<TodosReported>().Payload.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Should_drop_todo_items_without_content_and_treat_unknown_statuses_as_pending()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(CreateTodosEvent(new
        {
            items = new object[]
            {
                new { content = "   ", status = "completed" },
                new { status = "completed" },
                new { content = "Ship it", status = "blocked" },
                new { content = "Tidy up" },
            }
        }));

        result.ShouldBeOfType<TodosReported>().Payload.Items.Select(item => (item.Content, item.Status)).ShouldBe(
        [
            ("Ship it", TodoStatuses.Pending),
            ("Tidy up", TodoStatuses.Pending),
        ]);
    }

    [Fact]
    public void Should_return_null_for_a_todos_reported_event_without_a_payload()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.TodosReported,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow
        });

        result.ShouldBeNull();
    }

    [Fact]
    public void Should_classify_todos_reported_as_known_but_not_broadcast()
    {
        var classification = EventTypeMetadata.Classify(EventTypes.TodosReported);

        classification.IsKnown.ShouldBeTrue();
        classification.IsDurable.ShouldBeFalse();
        classification.IsEphemeralRelay.ShouldBeFalse();
    }

    private static readonly string[] WrittenPaths = ["/work/a.md", "/work/a.md", " ", "/work/b.cs"];

    [Fact]
    public void Should_translate_files_written_for_the_routed_fleet_session()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.FilesWritten,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { messageId = "msg-1", paths = WrittenPaths }),
        });

        var written = result.ShouldBeOfType<FilesWritten>();
        written.Payload.SessionId.ShouldBe("fleet-1");
        written.Payload.MessageId.ShouldBe("msg-1");
        written.Payload.Paths.ShouldBe(["/work/a.md", "/work/b.cs"]);
    }

    [Fact]
    public void Should_return_null_for_files_written_without_paths()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.FilesWritten,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { paths = Array.Empty<string>() }),
        });

        result.ShouldBeNull();
    }

    private static DomainEventTranslator CreateTranslator()
        => new(NullLogger<DomainEventTranslator>.Instance);

    private static HarnessEvent CreateTodosEvent(object payload)
        => new()
        {
            Type = EventTypes.TodosReported,
            SessionId = "harness-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };

    private static HarnessEvent CreateDelegationEvent(string eventType, object payload)
        => new()
        {
            Type = eventType,
            SessionId = "harness-parent",
            FleetSessionId = "fleet-parent",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };

    // --- Failed turns ------------------------------------------------------
    //
    // A turn that dies mid-flight (provider 5xx, reset connection, exhausted quota) used to reach the
    // client as nothing at all: the session simply went idle behind whatever text had already streamed.
    // These cover the shapes the harnesses actually send.

    [Fact]
    public void Should_translate_an_opencode_session_error_to_turn_failed()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionError,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = "oc-1",
                error = new
                {
                    name = "APIError",
                    data = new
                    {
                        message = "Connection reset by server",
                        isRetryable = true,
                        metadata = new { code = "ECONNRESET" },
                    },
                },
            }),
        });

        var failed = result.ShouldBeOfType<TurnFailed>();
        failed.Payload.SessionId.ShouldBe("fleet-1");
        failed.Payload.Error.Name.ShouldBe("APIError");
        failed.Payload.Error.Message.ShouldBe("Connection reset by server");
        failed.Payload.Error.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public void Should_translate_a_flat_session_error_to_turn_failed()
    {
        // Pi sends { "message": "..." } with no wrapper.
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionError,
            SessionId = "pi-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { message = "Pi assistant message was aborted." }),
        });

        var failed = result.ShouldBeOfType<TurnFailed>();
        failed.Payload.Error.Message.ShouldBe("Pi assistant message was aborted.");
        failed.Payload.Error.Name.ShouldBe("Error");
        failed.Payload.Error.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public void Should_translate_a_session_error_that_only_names_the_failure()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionError,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { error = new { name = "ProviderAuthError" } }),
        });

        var failed = result.ShouldBeOfType<TurnFailed>();
        failed.Payload.Error.Name.ShouldBe("ProviderAuthError");
        failed.Payload.Error.Message.ShouldBe("ProviderAuthError");
    }

    [Fact]
    public void Should_drop_a_session_error_with_nothing_to_show()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionError,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { error = new { } }),
        });

        result.ShouldBeNull();
    }

    [Fact]
    public void Should_name_the_assistant_message_a_failed_turn_belongs_to()
    {
        var translator = CreateTranslator();

        translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg_1",
                    role = "assistant",
                    sessionID = "oc-1",
                    time = new { created = 1L },
                },
                parts = Array.Empty<object>(),
            }),
        });

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.SessionError,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                error = new { name = "APIError", data = new { message = "boom" } },
            }),
        });

        result.ShouldBeOfType<TurnFailed>().Payload.MessageId.ShouldBe("msg_1");
    }

    [Fact]
    public void Should_carry_a_harness_message_error_onto_the_message_info()
    {
        // The error also rides on the message, so a reloaded session still shows why the turn stopped.
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg_1",
                    role = "assistant",
                    sessionID = "oc-1",
                    time = new { created = 1L },
                    finish = "error",
                    error = new
                    {
                        name = "APIError",
                        data = new { message = "Connection reset by server", isRetryable = true },
                    },
                },
                parts = Array.Empty<object>(),
            }),
        });

        var updated = result.ShouldBeOfType<MessageUpdated>();
        updated.Payload.Info.Error.ShouldNotBeNull();
        updated.Payload.Info.Error!.Message.ShouldBe("Connection reset by server");
        updated.Payload.Info.Finish.ShouldBe("error");
    }

    [Fact]
    public void Should_leave_a_healthy_message_without_an_error()
    {
        var translator = CreateTranslator();

        var result = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "oc-1",
            FleetSessionId = "fleet-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg_1",
                    role = "assistant",
                    sessionID = "oc-1",
                    time = new { created = 1L },
                    finish = "stop",
                },
                parts = Array.Empty<object>(),
            }),
        });

        var updated = result.ShouldBeOfType<MessageUpdated>();
        updated.Payload.Info.Error.ShouldBeNull();
        updated.Payload.Info.Finish.ShouldBe("stop");
    }

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
