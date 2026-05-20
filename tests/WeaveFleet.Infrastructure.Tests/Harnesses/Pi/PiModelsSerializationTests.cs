using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

public sealed class PiModelsSerializationTests
{
    private static readonly JsonSerializerOptions Options = PiJsonOptions.Default;

    [Fact]
    public void command_types_round_trip_with_expected_json()
    {
        var cases = new (PiCommand Value, string ExpectedJson, Type ExpectedType)[]
        {
            (new PiPromptCommand { Id = "cmd-prompt", Message = "hello" },
                """{"type":"prompt","id":"cmd-prompt","message":"hello"}""", typeof(PiPromptCommand)),
            (new PiSteerCommand { Id = "cmd-steer", Message = "go left" },
                """{"type":"steer","id":"cmd-steer","message":"go left"}""", typeof(PiSteerCommand)),
            (new PiFollowUpCommand { Id = "cmd-follow", Message = "next" },
                """{"type":"follow_up","id":"cmd-follow","message":"next"}""", typeof(PiFollowUpCommand)),
            (new PiAbortCommand { Id = "cmd-abort" },
                """{"type":"abort","id":"cmd-abort"}""", typeof(PiAbortCommand)),
            (new PiGetStateCommand { Id = "cmd-state" },
                """{"type":"get_state","id":"cmd-state"}""", typeof(PiGetStateCommand)),
            (new PiGetMessagesCommand { Id = "cmd-messages" },
                """{"type":"get_messages","id":"cmd-messages"}""", typeof(PiGetMessagesCommand)),
            (new PiSetModelCommand { Id = "cmd-model", Provider = "openrouter", Model = "anthropic/claude" },
                """{"type":"set_model","id":"cmd-model","provider":"openrouter","model":"anthropic/claude"}""", typeof(PiSetModelCommand)),
            (new PiSetThinkingLevelCommand { Id = "cmd-thinking", ThinkingLevel = "high" },
                """{"type":"set_thinking_level","id":"cmd-thinking","thinkingLevel":"high"}""", typeof(PiSetThinkingLevelCommand)),
            (new PiCompactCommand { Id = "cmd-compact" },
                """{"type":"compact","id":"cmd-compact"}""", typeof(PiCompactCommand)),
            (new PiBashCommand { Id = "cmd-bash", Command = "pwd" },
                """{"type":"bash","id":"cmd-bash","command":"pwd"}""", typeof(PiBashCommand)),
            (new PiNewSessionCommand { Id = "cmd-new" },
                """{"type":"new_session","id":"cmd-new"}""", typeof(PiNewSessionCommand)),
            (new PiForkCommand { Id = "cmd-fork" },
                """{"type":"fork","id":"cmd-fork"}""", typeof(PiForkCommand)),
            (new PiCloneCommand { Id = "cmd-clone" },
                """{"type":"clone","id":"cmd-clone"}""", typeof(PiCloneCommand)),
            (new PiSwitchSessionCommand { Id = "cmd-switch", SessionPath = "/tmp/pi/session.json" },
                """{"type":"switch_session","id":"cmd-switch","sessionPath":"/tmp/pi/session.json"}""", typeof(PiSwitchSessionCommand)),
        };

        foreach (var (value, expectedJson, expectedType) in cases)
        {
            var json = JsonSerializer.Serialize(value, PiJsonContext.Default.PiCommand);
            AssertJsonEquals(expectedJson, json);

            var roundTripped = JsonSerializer.Deserialize(json, PiJsonContext.Default.PiCommand);

            roundTripped.ShouldNotBeNull();
            roundTripped.GetType().ShouldBe(expectedType);
            AssertJsonEquals(expectedJson, JsonSerializer.Serialize(roundTripped, PiJsonContext.Default.PiCommand));
        }
    }

    [Fact]
    public void event_types_round_trip_with_expected_json()
    {
        var cases = new (PiEvent Value, string ExpectedJson, Type ExpectedType)[]
        {
            (new PiResponseEvent { Id = "req-1", Command = "get_state", Success = true, Data = JsonElement("""{"ok":true}""") },
                """{"type":"response","id":"req-1","command":"get_state","success":true,"data":{"ok":true}}""", typeof(PiResponseEvent)),
            (new PiAgentStartEvent(),
                """{"type":"agent_start"}""", typeof(PiAgentStartEvent)),
            (new PiAgentEndEvent { Messages = [SimpleMessage()] },
                """{"type":"agent_end","messages":[{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}]}""", typeof(PiAgentEndEvent)),
            (new PiTurnStartEvent(),
                """{"type":"turn_start"}""", typeof(PiTurnStartEvent)),
            (new PiTurnEndEvent { Message = SimpleMessage(), ToolResults = [SimpleToolResult()] },
                """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1},"toolResults":[{"content":[{"type":"text","text":"tool output"}],"details":{"exitCode":0}}]}""", typeof(PiTurnEndEvent)),
            (new PiMessageStartEvent { Message = SimpleMessage() },
                """{"type":"message_start","message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}}""", typeof(PiMessageStartEvent)),
            (new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextDeltaEvent { ContentIndex = 0, Delta = "hel" }, Message = SimpleMessage() },
                """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"hel"},"message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}}""", typeof(PiMessageUpdateEvent)),
            (new PiMessageEndEvent { Message = SimpleMessage() },
                """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}}""", typeof(PiMessageEndEvent)),
            (new PiToolExecutionStartEvent { ToolCallId = "tool-1", ToolName = "bash", Args = JsonElement("""{"command":"ls"}""") },
                """{"type":"tool_execution_start","toolCallId":"tool-1","toolName":"bash","args":{"command":"ls"}}""", typeof(PiToolExecutionStartEvent)),
            (new PiToolExecutionUpdateEvent { ToolCallId = "tool-1", ToolName = "bash", Args = JsonElement("""{"command":"ls"}"""), PartialResult = SimpleToolResult() },
                """{"type":"tool_execution_update","toolCallId":"tool-1","toolName":"bash","args":{"command":"ls"},"partialResult":{"content":[{"type":"text","text":"tool output"}],"details":{"exitCode":0}}}""", typeof(PiToolExecutionUpdateEvent)),
            (new PiToolExecutionEndEvent { ToolCallId = "tool-1", ToolName = "bash", Result = SimpleToolResult(), IsError = false },
                """{"type":"tool_execution_end","toolCallId":"tool-1","toolName":"bash","result":{"content":[{"type":"text","text":"tool output"}],"details":{"exitCode":0}},"isError":false}""", typeof(PiToolExecutionEndEvent)),
            (new PiCompactionStartEvent { Message = "compacting" },
                """{"type":"compaction_start","message":"compacting"}""", typeof(PiCompactionStartEvent)),
            (new PiCompactionEndEvent { Message = "done", Success = true },
                """{"type":"compaction_end","message":"done","success":true}""", typeof(PiCompactionEndEvent)),
            (new PiAutoRetryStartEvent { Reason = "rate_limit", Attempt = 2, DelayMs = 500 },
                """{"type":"auto_retry_start","reason":"rate_limit","attempt":2,"delayMs":500}""", typeof(PiAutoRetryStartEvent)),
            (new PiAutoRetryEndEvent { Success = true, Attempt = 2 },
                """{"type":"auto_retry_end","success":true,"attempt":2}""", typeof(PiAutoRetryEndEvent)),
            (new PiQueueUpdateEvent { PendingMessageCount = 3 },
                """{"type":"queue_update","pendingMessageCount":3}""", typeof(PiQueueUpdateEvent)),
            (new PiIdleEvent(),
                """{"type":"idle"}""", typeof(PiIdleEvent)),
            (new PiErrorEvent { Error = "boom", Message = "failed" },
                """{"type":"error","error":"boom","message":"failed"}""", typeof(PiErrorEvent)),
            (new PiLogEvent { Level = "info", Message = "ready" },
                """{"type":"log","level":"info","message":"ready"}""", typeof(PiLogEvent)),
            (new PiSessionSwitchedEvent { SessionFile = "/tmp/pi/session.json", SessionId = "sess-1" },
                """{"type":"session_switched","sessionFile":"/tmp/pi/session.json","sessionId":"sess-1"}""", typeof(PiSessionSwitchedEvent)),
            (new PiStateUpdateEvent { State = SimpleState() },
                """{"type":"state_update","state":{"thinkingLevel":"medium","isStreaming":false,"isCompacting":false,"sessionFile":"/tmp/pi/session.json","sessionId":"sess-1","autoCompactionEnabled":true,"messageCount":5,"pendingMessageCount":1}}""", typeof(PiStateUpdateEvent)),
        };

        foreach (var (value, expectedJson, expectedType) in cases)
        {
            var json = JsonSerializer.Serialize(value, PiJsonContext.Default.PiEvent);
            AssertJsonEquals(expectedJson, json);

            var roundTripped = JsonSerializer.Deserialize(json, PiJsonContext.Default.PiEvent);

            roundTripped.ShouldNotBeNull();
            roundTripped.GetType().ShouldBe(expectedType);
            AssertJsonEquals(expectedJson, JsonSerializer.Serialize(roundTripped, PiJsonContext.Default.PiEvent));
        }
    }

    [Fact]
    public void response_event_deserializes_when_type_discriminator_is_not_first()
    {
        const string json =
            """
            {"id":"probe","type":"response","command":"get_state","success":true,"data":{"model":{"id":"claude-haiku-4.5","name":"Claude Haiku 4.5","api":"anthropic-messages","provider":"github-copilot","baseUrl":"https://api.individual.githubcopilot.com","headers":{"User-Agent":"GitHubCopilotChat/0.35.0","Editor-Version":"vscode/1.107.0","Editor-Plugin-Version":"copilot-chat/0.35.0","Copilot-Integration-Id":"vscode-chat"},"compat":{"supportsEagerToolInputStreaming":false},"reasoning":true,"input":["text","image"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":144000,"maxTokens":32000},"thinkingLevel":"medium","isStreaming":false,"isCompacting":false,"steeringMode":"one-at-a-time","followUpMode":"one-at-a-time","sessionFile":"/Users/example/.pi/agent/sessions/session.jsonl","sessionId":"019e43f1-79f1-753f-823e-77901b13ff9c","autoCompactionEnabled":true,"messageCount":0,"pendingMessageCount":0}}
            """;

        var normalized = NormalizeDiscriminatorsFirst(json);
        var piEvent = JsonSerializer.Deserialize(normalized, PiJsonContext.Default.PiEvent);

        var response = piEvent.ShouldBeOfType<PiResponseEvent>();
        response.Id.ShouldBe("probe");
        response.Command.ShouldBe("get_state");
        response.Success.ShouldBeTrue();
        response.Data.ShouldNotBeNull();

        var state = JsonSerializer.Deserialize(response.Data.Value, PiJsonContext.Default.PiState);
        state.ShouldNotBeNull();
        state.Model.ShouldNotBeNull();
        state.Model.Id.ShouldBe("claude-haiku-4.5");
        state.Model.Provider.ShouldBe("github-copilot");
        state.SessionId.ShouldBe("019e43f1-79f1-753f-823e-77901b13ff9c");
    }

    [Fact]
    public void message_update_deserializes_when_nested_type_discriminator_is_not_first()
    {
        const string json =
            """
            {"type":"message_update","assistantMessageEvent":{"contentIndex":0,"delta":"OK","type":"text_delta"},"message":{"role":"assistant","content":[{"text":"OK","type":"text"}],"timestamp":1}}
            """;

        var normalized = NormalizeDiscriminatorsFirst(json);
        var piEvent = JsonSerializer.Deserialize(normalized, PiJsonContext.Default.PiEvent);

        var messageUpdate = piEvent.ShouldBeOfType<PiMessageUpdateEvent>();
        var textDelta = messageUpdate.AssistantMessageEvent.ShouldBeOfType<PiTextDeltaEvent>();
        textDelta.Delta.ShouldBe("OK");
        var text = messageUpdate.Message.ShouldNotBeNull().Content.Single().ShouldBeOfType<PiTextContent>();
        text.Text.ShouldBe("OK");
    }

    [Fact]
    public void assistant_message_event_types_round_trip_with_expected_json()
    {
        var cases = new (PiAssistantMessageEvent Value, string ExpectedJson, Type ExpectedType)[]
        {
            (new PiThinkingStartEvent { ContentIndex = 1, Partial = SimpleMessage() },
                """{"type":"thinking_start","contentIndex":1,"partial":{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}}""", typeof(PiThinkingStartEvent)),
            (new PiThinkingDeltaEvent { ContentIndex = 1, Delta = "think" },
                """{"type":"thinking_delta","contentIndex":1,"delta":"think"}""", typeof(PiThinkingDeltaEvent)),
            (new PiThinkingEndEvent { ContentIndex = 1, Content = "thought" },
                """{"type":"thinking_end","contentIndex":1,"content":"thought"}""", typeof(PiThinkingEndEvent)),
            (new PiTextStartEvent { ContentIndex = 2 },
                """{"type":"text_start","contentIndex":2}""", typeof(PiTextStartEvent)),
            (new PiTextDeltaEvent { ContentIndex = 2, Delta = "hello" },
                """{"type":"text_delta","contentIndex":2,"delta":"hello"}""", typeof(PiTextDeltaEvent)),
            (new PiTextEndEvent { ContentIndex = 2, Content = "hello" },
                """{"type":"text_end","contentIndex":2,"content":"hello"}""", typeof(PiTextEndEvent)),
            (new PiToolCallStartEvent { ContentIndex = 3 },
                """{"type":"toolcall_start","contentIndex":3}""", typeof(PiToolCallStartEvent)),
            (new PiToolCallDeltaEvent { ContentIndex = 3, Delta = "{\"command\"" },
                """{"type":"toolcall_delta","contentIndex":3,"delta":"{\"command\""}""", typeof(PiToolCallDeltaEvent)),
            (new PiToolCallEndEvent { ContentIndex = 3, ToolCall = ToolCallContent() },
                """{"type":"toolcall_end","contentIndex":3,"toolCall":{"id":"call-1","name":"bash","arguments":{"command":"ls"},"partialArgs":"{\"command\"","streamIndex":0}}""", typeof(PiToolCallEndEvent)),
        };

        foreach (var (value, expectedJson, expectedType) in cases)
        {
            AssertPolymorphicRoundTrip(value, expectedJson, expectedType);
        }
    }

    [Fact]
    public void content_block_types_round_trip_with_expected_json()
    {
        var cases = new (PiContentBlock Value, string ExpectedJson, Type ExpectedType)[]
        {
            (new PiTextContent { Text = "hello" },
                """{"type":"text","text":"hello"}""", typeof(PiTextContent)),
            (new PiThinkingContent { Thinking = "reasoning", ThinkingSignature = "sig-1" },
                """{"type":"thinking","thinking":"reasoning","thinkingSignature":"sig-1"}""", typeof(PiThinkingContent)),
            (ToolCallContent(),
                """{"type":"toolCall","id":"call-1","name":"bash","arguments":{"command":"ls"},"partialArgs":"{\"command\"","streamIndex":0}""", typeof(PiToolCallContent)),
        };

        foreach (var (value, expectedJson, expectedType) in cases)
        {
            AssertPolymorphicRoundTrip(value, expectedJson, expectedType);
        }
    }

    [Fact]
    public void payload_model_types_round_trip_with_expected_json()
    {
        AssertRoundTrip(new PiMessage
        {
            Role = "assistant",
            Content =
            [
                new PiTextContent { Text = "hello" },
                new PiThinkingContent { Thinking = "reasoning", ThinkingSignature = "sig-1" },
                ToolCallContent(),
            ],
            Api = "chat",
            Provider = "openrouter",
            Model = "claude",
            Usage = Usage(),
            StopReason = "end_turn",
            Timestamp = 123,
            ResponseId = "resp-1",
            ResponseModel = "claude-actual",
            ErrorMessage = "none",
            ToolCallId = "call-1",
            ToolName = "bash",
            IsError = false,
        },
        """{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"thinking","thinking":"reasoning","thinkingSignature":"sig-1"},{"type":"toolCall","id":"call-1","name":"bash","arguments":{"command":"ls"},"partialArgs":"{\"command\"","streamIndex":0}],"api":"chat","provider":"openrouter","model":"claude","usage":{"input":10,"output":20,"cacheRead":3,"cacheWrite":4,"totalTokens":37,"cost":{"input":1.25,"output":2.5,"cacheRead":0.5,"cacheWrite":0.75,"total":5}},"stopReason":"end_turn","timestamp":123,"responseId":"resp-1","responseModel":"claude-actual","errorMessage":"none","toolCallId":"call-1","toolName":"bash","isError":false}""");

        AssertRoundTrip(SimpleToolResult(),
            """{"content":[{"type":"text","text":"tool output"}],"details":{"exitCode":0}}""");

        AssertRoundTrip(SimpleState() with
        {
            Model = new PiModelInfo
            {
                Id = "claude",
                Name = "Claude",
                Api = "chat",
                Provider = "openrouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                Reasoning = true,
                Input = ["text", "image"],
                Cost = Cost(),
                ContextWindow = 200000,
                MaxTokens = 8192,
            },
            SteeringMode = "instant",
            FollowUpMode = "queue",
        },
        """{"model":{"id":"claude","name":"Claude","api":"chat","provider":"openrouter","baseUrl":"https://openrouter.ai/api/v1","reasoning":true,"input":["text","image"],"cost":{"input":1.25,"output":2.5,"cacheRead":0.5,"cacheWrite":0.75,"total":5},"contextWindow":200000,"maxTokens":8192},"thinkingLevel":"medium","isStreaming":false,"isCompacting":false,"steeringMode":"instant","followUpMode":"queue","sessionFile":"/tmp/pi/session.json","sessionId":"sess-1","autoCompactionEnabled":true,"messageCount":5,"pendingMessageCount":1}""");

        AssertRoundTrip(new PiModelInfo
        {
            Id = "claude",
            Name = "Claude",
            Api = "chat",
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Reasoning = true,
            Input = ["text"],
            Cost = Cost(),
            ContextWindow = 200000,
            MaxTokens = 8192,
        },
        """{"id":"claude","name":"Claude","api":"chat","provider":"openrouter","baseUrl":"https://openrouter.ai/api/v1","reasoning":true,"input":["text"],"cost":{"input":1.25,"output":2.5,"cacheRead":0.5,"cacheWrite":0.75,"total":5},"contextWindow":200000,"maxTokens":8192}""");

        AssertRoundTrip(Cost(),
            """{"input":1.25,"output":2.5,"cacheRead":0.5,"cacheWrite":0.75,"total":5}""");

        AssertRoundTrip(Usage(),
            """{"input":10,"output":20,"cacheRead":3,"cacheWrite":4,"totalTokens":37,"cost":{"input":1.25,"output":2.5,"cacheRead":0.5,"cacheWrite":0.75,"total":5}}""");
    }

    [Fact]
    public void source_generated_response_payloads_round_trip_with_expected_json()
    {
        var messagesResponse = new PiGetMessagesResponse
        {
            Messages = [SimpleMessage()],
            HasMore = true,
        };
        const string messagesResponseJson = """{"messages":[{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}],"hasMore":true}""";

        var messagesResponseSerialized = JsonSerializer.Serialize(messagesResponse, PiJsonContext.Default.PiGetMessagesResponse);
        AssertJsonEquals(messagesResponseJson, messagesResponseSerialized);
        var messagesResponseRoundTripped = JsonSerializer.Deserialize(messagesResponseSerialized, PiJsonContext.Default.PiGetMessagesResponse);
        messagesResponseRoundTripped.ShouldNotBeNull();
        AssertJsonEquals(messagesResponseJson, JsonSerializer.Serialize(messagesResponseRoundTripped, PiJsonContext.Default.PiGetMessagesResponse));

        PiMessage[] messages = [SimpleMessage()];
        const string messagesJson = """[{"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1}]""";

        var messagesSerialized = JsonSerializer.Serialize(messages, PiJsonContext.Default.PiMessageArray);
        AssertJsonEquals(messagesJson, messagesSerialized);
        var messagesRoundTripped = JsonSerializer.Deserialize(messagesSerialized, PiJsonContext.Default.PiMessageArray);
        messagesRoundTripped.ShouldNotBeNull();
        messagesRoundTripped.Length.ShouldBe(1);
        AssertJsonEquals(messagesJson, JsonSerializer.Serialize(messagesRoundTripped, PiJsonContext.Default.PiMessageArray));

        var resumeToken = new PiResumeToken { SessionFile = "/tmp/pi/session.json", SessionId = "sess-1" };
        const string resumeTokenJson = """{"sessionFile":"/tmp/pi/session.json","sessionId":"sess-1"}""";

        var resumeTokenSerialized = JsonSerializer.Serialize(resumeToken, PiJsonContext.Default.PiResumeToken);
        AssertJsonEquals(resumeTokenJson, resumeTokenSerialized);
        var resumeTokenRoundTripped = JsonSerializer.Deserialize(resumeTokenSerialized, PiJsonContext.Default.PiResumeToken);
        resumeTokenRoundTripped.ShouldNotBeNull();
        AssertJsonEquals(resumeTokenJson, JsonSerializer.Serialize(resumeTokenRoundTripped, PiJsonContext.Default.PiResumeToken));
    }

    private static void AssertPolymorphicRoundTrip<TBase>(TBase value, string expectedJson, Type expectedType)
        where TBase : class
    {
        var json = JsonSerializer.Serialize(value, Options);
        AssertJsonEquals(expectedJson, json);

        var roundTripped = JsonSerializer.Deserialize<TBase>(json, Options);

        roundTripped.ShouldNotBeNull();
        roundTripped.GetType().ShouldBe(expectedType);
        AssertJsonEquals(expectedJson, JsonSerializer.Serialize(roundTripped, Options));
    }

    private static void AssertRoundTrip<T>(T value, string expectedJson)
        where T : class
    {
        var json = JsonSerializer.Serialize(value, Options);
        AssertJsonEquals(expectedJson, json);

        var roundTripped = JsonSerializer.Deserialize<T>(json, Options);

        roundTripped.ShouldNotBeNull();
        AssertJsonEquals(expectedJson, JsonSerializer.Serialize(roundTripped, Options));
    }

    private static void AssertJsonEquals(string expectedJson, string actualJson)
    {
        var expected = JsonNode.Parse(expectedJson);
        var actual = JsonNode.Parse(actualJson);

        JsonNode.DeepEquals(expected, actual).ShouldBeTrue($"\nExpected: {expectedJson}\nActual:   {actualJson}");
    }

    private static JsonElement JsonElement(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json, Options);
    }

    private static string NormalizeDiscriminatorsFirst(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElementWithTypeFirst(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElementWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                if (element.TryGetProperty("type", out var type))
                {
                    writer.WritePropertyName("type");
                    type.WriteTo(writer);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("type"))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElementWithTypeFirst(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithTypeFirst(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static PiMessage SimpleMessage()
    {
        return new PiMessage
        {
            Role = "assistant",
            Content = [new PiTextContent { Text = "hello" }],
            Timestamp = 1,
        };
    }

    private static PiToolResult SimpleToolResult()
    {
        return new PiToolResult
        {
            Content = [new PiTextContent { Text = "tool output" }],
            Details = JsonElement("""{"exitCode":0}"""),
        };
    }

    private static PiToolCallContent ToolCallContent()
    {
        return new PiToolCallContent
        {
            Id = "call-1",
            Name = "bash",
            Arguments = JsonElement("""{"command":"ls"}"""),
            PartialArgs = "{\"command\"",
            StreamIndex = 0,
        };
    }

    private static PiState SimpleState()
    {
        return new PiState
        {
            ThinkingLevel = "medium",
            IsStreaming = false,
            IsCompacting = false,
            SessionFile = "/tmp/pi/session.json",
            SessionId = "sess-1",
            AutoCompactionEnabled = true,
            MessageCount = 5,
            PendingMessageCount = 1,
        };
    }

    private static PiUsage Usage()
    {
        return new PiUsage
        {
            Input = 10,
            Output = 20,
            CacheRead = 3,
            CacheWrite = 4,
            TotalTokens = 37,
            Cost = Cost(),
        };
    }

    private static PiCost Cost()
    {
        return new PiCost
        {
            Input = 1.25,
            Output = 2.5,
            CacheRead = 0.5,
            CacheWrite = 0.75,
            Total = 5,
        };
    }
}
