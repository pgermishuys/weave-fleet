using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Domain.Tests.Events;

public sealed class DomainEventSerializationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static TheoryData<string, DomainEvent> RoundTripCases
    {
        get
        {
            var cases = new TheoryData<string, DomainEvent>();

            cases.Add("session.started", new SessionStarted
            {
                Payload = new SessionStartedPayload
                {
                    SessionId = "session-1",
                    InstanceId = "instance-1",
                    WorkspaceId = "workspace-1",
                    Title = "Test Session",
                    ProjectId = "project-1",
                    ParentSessionId = "session-parent",
                    IsHidden = false,
                },
            });

            cases.Add("session.idled", new SessionIdled
            {
                Payload = new SessionIdledPayload
                {
                    SessionId = "session-1",
                },
            });

            cases.Add("session.stopped", new SessionStopped
            {
                Payload = new SessionStoppedPayload
                {
                    SessionId = "session-1",
                    StoppedAt = "2026-05-16T10:11:12.0000000Z",
                },
            });

            cases.Add("session.deleted", new SessionDeleted
            {
                Payload = new SessionDeletedPayload
                {
                    SessionId = "session-1",
                },
            });

            cases.Add("session.archived", new SessionArchived
            {
                Payload = new SessionArchivedPayload
                {
                    SessionId = "session-1",
                    ArchivedAt = "2026-05-16T10:11:12.0000000Z",
                },
            });

            cases.Add("turn.started", new TurnStarted
            {
                Payload = new TurnStartedPayload
                {
                    SessionId = "session-1",
                    MessageId = "message-1",
                    Index = 0,
                    Agent = "loom",
                    ModelId = "anthropic/claude-sonnet-4-5",
                    ParentId = "message-0",
                },
            });

            cases.Add("turn.ended", new TurnEnded
            {
                Payload = new TurnEndedPayload
                {
                    SessionId = "session-1",
                    MessageId = "message-1",
                    Index = 0,
                    Reason = "completed",
                    Cost = 0.42,
                    Tokens = new TurnTokenUsage
                    {
                        Input = 12,
                        Output = 24,
                        Reasoning = 6,
                    },
                    CompletedAt = 1_700_000_000_123,
                },
            });

            cases.Add("message.created", new MessageCreated
            {
                Payload = new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "message-1",
                        Role = "assistant",
                        SessionId = "session-1",
                        Agent = "loom",
                        ModelId = "anthropic/claude-sonnet-4-5",
                        ParentId = "message-0",
                        Time = new MessageEventTime
                        {
                            Created = 1_700_000_000_000,
                            Completed = 1_700_000_000_456,
                        },
                        Cost = 0.84,
                        Tokens = new MessageTokenUsage
                        {
                            Input = 30,
                            Output = 60,
                            Reasoning = 10,
                        },
                    },
                    Parts =
                    [
                        new TextMessageEventPart
                        {
                            Id = "part-1",
                            SessionId = "session-1",
                            MessageId = "message-1",
                            Text = "Hello world",
                        },
                        new ToolMessageEventPart
                        {
                            Id = "part-2",
                            SessionId = "session-1",
                            MessageId = "message-1",
                            ToolName = "bash",
                            CallId = "call-1",
                            State = new ToolCompletedState
                            {
                                Input = JsonSerializer.SerializeToElement(new { command = "ls" }),
                                Output = JsonSerializer.SerializeToElement(new { result = "file.txt" }),
                                Metadata = JsonSerializer.SerializeToElement(new { exitCode = 0 }),
                            },
                        },
                    ],
                },
            });

            cases.Add("message.updated", new MessageUpdated
            {
                Payload = new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "message-2",
                        Role = "assistant",
                        SessionId = "session-1",
                        Agent = "thread",
                        ModelId = "openai/gpt-4.1",
                        Time = new MessageEventTime
                        {
                            Created = 1_700_000_001_000,
                        },
                    },
                    Parts =
                    [
                        new ReasoningMessageEventPart
                        {
                            Id = "part-3",
                            SessionId = "session-1",
                            MessageId = "message-2",
                            Text = "Thinking through the options.",
                            Summary = "Compare approaches",
                        },
                        new FileMessageEventPart
                        {
                            Id = "part-4",
                            SessionId = "session-1",
                            MessageId = "message-2",
                            Mime = "image/png",
                            Url = "https://example.test/image.png",
                            Filename = "image.png",
                        },
                    ],
                },
            });

            cases.Add("message.part.updated", new MessagePartUpdated
            {
                Payload = new MessagePartUpdatedPayload
                {
                    SessionId = "session-1",
                    Part = new StepFinishedMessageEventPart
                    {
                        Id = "part-5",
                        SessionId = "session-1",
                        MessageId = "message-2",
                        Index = 1,
                        Reason = "stop",
                        Cost = 1.25,
                        Tokens = new MessageTokenUsage
                        {
                            Input = 25,
                            Output = 75,
                            Reasoning = 15,
                        },
                        CompletedAt = 1_700_000_002_000,
                    },
                },
            });

            cases.Add("message.part.delta.streamed", new MessagePartDeltaStreamed
            {
                Payload = new MessagePartDeltaStreamedPayload
                {
                    SessionId = "session-1",
                    MessageId = "message-2",
                    PartId = "part-1",
                    Field = "text",
                    Delta = " world",
                },
            });

            cases.Add("delegation.created", new DelegationCreated
            {
                Payload = new DelegationCreatedPayload
                {
                    DelegationId = "delegation-1",
                    ParentSessionId = "session-1",
                    ParentToolCallId = "call-1",
                    ChildSessionId = null,
                    Title = "Review pull request",
                    Status = "pending",
                    CreatedAt = "2026-05-16T10:11:12.0000000Z",
                },
            });

            cases.Add("delegation.updated", new DelegationUpdated
            {
                Payload = new DelegationUpdatedPayload
                {
                    DelegationId = "delegation-1",
                    ParentSessionId = "session-1",
                    ParentToolCallId = "call-1",
                    ChildSessionId = "session-child-1",
                    Title = "Review pull request",
                    Status = "running",
                    CreatedAt = "2026-05-16T10:11:12.0000000Z",
                },
            });

            cases.Add("delegation.completed", new DelegationCompleted
            {
                Payload = new DelegationCompletedPayload
                {
                    DelegationId = "delegation-1",
                    ParentSessionId = "session-1",
                    ParentToolCallId = "call-1",
                    ChildSessionId = "session-child-1",
                    Title = "Review pull request",
                    Status = "completed",
                    CreatedAt = "2026-05-16T10:11:12.0000000Z",
                    CompletedAt = "2026-05-16T10:15:12.0000000Z",
                },
            });

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Should_round_trip_domain_event_json(string discriminator, DomainEvent expected)
    {
        var json = JsonSerializer.Serialize(expected, SerializerOptions);

        using (var document = JsonDocument.Parse(json))
        {
            document.RootElement.GetProperty("type").GetString().ShouldBe(discriminator);
            document.RootElement.TryGetProperty("payload", out _).ShouldBeTrue();
        }

        var actual = JsonSerializer.Deserialize<DomainEvent>(json, SerializerOptions);

        actual.ShouldNotBeNull();
        actual.GetType().ShouldBe(expected.GetType());
        JsonSerializer.Serialize(actual, SerializerOptions).ShouldBe(json);
    }
}
