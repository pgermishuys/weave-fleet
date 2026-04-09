using System.Text.Json;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeModelsSerializationTests
{
    private static readonly JsonSerializerOptions Options = OpenCodeJsonOptions.Default;

    [Fact]
    public void HealthResponse_RoundTrips()
    {
        const string json = """{"healthy":true,"version":"1.2.3"}""";
        var result = JsonSerializer.Deserialize<OpenCodeHealthResponse>(json, Options);

        result.ShouldNotBeNull();
        result.Healthy.ShouldBeTrue();
        result.Version.ShouldBe("1.2.3");

        var roundTripped = JsonSerializer.Serialize(result, Options);
        roundTripped.ShouldContain("\"healthy\":true");
        roundTripped.ShouldContain("\"version\":\"1.2.3\"");
    }

    [Fact]
    public void SessionInfo_RoundTrips_WithNulls()
    {
        const string json = """{"id":"sess-1","version":"1","time":{"created":1000000,"updated":2000000}}""";
        var result = JsonSerializer.Deserialize<OpenCodeSessionInfo>(json, Options);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("sess-1");
        result.Version.ShouldBe("1");
        result.Title.ShouldBeNull();
        result.Time!.Created.ShouldBe(1000000L);
    }

    [Fact]
    public void SessionInfo_RoundTrips_WithAllFields()
    {
        const string json = """
        {
          "id":"sess-2",
          "slug":"my-session",
          "projectId":"proj-1",
          "directory":"/home/user/project",
          "title":"My Session",
          "version":"3",
          "time":{"created":1000000,"updated":2000000,"compacting":1500000},
          "parentId":"sess-0",
          "workspaceId":"ws-1",
          "summary":"A summary"
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeSessionInfo>(json, Options);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("sess-2");
        result.Slug.ShouldBe("my-session");
        result.Title.ShouldBe("My Session");
        result.ParentId.ShouldBe("sess-0");
        result.Time!.Compacting.ShouldBe(1500000L);
    }

    [Fact]
    public void UserMessage_Deserializes()
    {
        const string json = """
        {
          "role":"user",
          "id":"msg-1",
          "sessionId":"sess-1",
          "time":{"created":1000000},
          "agent":"default"
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageInfo>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldBeOfType<OpenCodeUserMessage>();
        var user = (OpenCodeUserMessage)result;
        user.Id.ShouldBe("msg-1");
        user.Role.ShouldBe("user");
        user.Agent.ShouldBe("default");
    }

    [Fact]
    public void AssistantMessage_Deserializes()
    {
        const string json = """
        {
          "role":"assistant",
          "id":"msg-2",
          "sessionId":"sess-1",
          "time":{"created":2000000,"completed":2500000},
          "providerId":"openai",
          "modelId":"gpt-4o",
          "agent":"default",
          "cost":0.001,
          "finish":"end_turn"
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageInfo>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldBeOfType<OpenCodeAssistantMessage>();
        var assistant = (OpenCodeAssistantMessage)result;
        assistant.ModelId.ShouldBe("gpt-4o");
        assistant.ProviderId.ShouldBe("openai");
        assistant.Finish.ShouldBe("end_turn");
        assistant.Cost.ShouldBe(0.001);
        assistant.Time.Completed.ShouldBe(2500000L);
    }

    [Fact]
    public void MessageParts_TextPart_Deserializes()
    {
        const string json = """
        {
          "type":"text",
          "id":"part-1",
          "sessionId":"sess-1",
          "messageId":"msg-1",
          "text":"Hello world"
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessagePart>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldBeOfType<OpenCodeTextPart>();
        ((OpenCodeTextPart)result).Text.ShouldBe("Hello world");
    }

    [Fact]
    public void MessageParts_ToolPart_Deserializes()
    {
        const string json = """
        {
          "type":"tool",
          "id":"part-2",
          "sessionId":"sess-1",
          "messageId":"msg-2",
          "callId":"call-1",
          "tool":"bash",
          "state":{"status":"completed","input":{"command":"ls"},"output":{"result":"file.txt"}}
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessagePart>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldBeOfType<OpenCodeToolPart>();
        var tool = (OpenCodeToolPart)result;
        tool.Tool.ShouldBe("bash");
        tool.CallId.ShouldBe("call-1");
        tool.State.ShouldBeOfType<OpenCodeToolCompleted>();
    }

    [Fact]
    public void ToolState_Discriminated_Deserializes()
    {
        var cases = new[]
        {
            ("""{"status":"pending"}""", typeof(OpenCodeToolPending)),
            ("""{"status":"running"}""", typeof(OpenCodeToolRunning)),
            ("""{"status":"completed"}""", typeof(OpenCodeToolCompleted)),
            ("""{"status":"error"}""", typeof(OpenCodeToolError)),
        };

        foreach (var (json, expectedType) in cases)
        {
            var result = JsonSerializer.Deserialize<OpenCodeToolState>(json, Options);
            result.ShouldNotBeNull();
            result.GetType().ShouldBe(expectedType);
        }
    }

    [Fact]
    public void PromptRequest_Serializes()
    {
        var request = new OpenCodePromptRequest
        {
            Parts = [new OpenCodePromptTextPart { Text = "Hello" }],
            Agent = "default",
        };

        var json = JsonSerializer.Serialize(request, Options);

        json.ShouldContain("\"parts\"");
        json.ShouldContain("\"type\":\"text\"");
        json.ShouldContain("\"text\":\"Hello\"");
        json.ShouldContain("\"agent\":\"default\"");
        // Null fields should be omitted
        json.ShouldNotContain("\"model\"");
    }

    [Fact]
    public void SseEvent_Deserializes()
    {
        const string json = """{"type":"session.status","properties":{"sessionId":"sess-1","status":"idle"}}""";

        var result = JsonSerializer.Deserialize<OpenCodeSseEvent>(json, Options);

        result.ShouldNotBeNull();
        result.Type.ShouldBe("session.status");
        result.Properties.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void SessionStatus_Discriminated_Deserializes()
    {
        var idle = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"idle"}""", Options);
        var busy = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"busy","since":1000000}""", Options);
        var retry = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"retry","reason":"rate limit","delay":2000,"count":1}""", Options);

        idle.ShouldBeOfType<OpenCodeIdleStatus>();
        busy.ShouldBeOfType<OpenCodeBusyStatus>();
        ((OpenCodeBusyStatus)busy!).Since.ShouldBe(1000000L);
        retry.ShouldBeOfType<OpenCodeRetryStatus>();
        ((OpenCodeRetryStatus)retry!).Reason.ShouldBe("rate limit");
        ((OpenCodeRetryStatus)retry!).Delay.ShouldBe(2000);
    }

    [Fact]
    public void ProvidersResponse_Deserializes()
    {
        const string json = """
        {
          "all":[
            {
              "id":"openai",
              "name":"OpenAI",
              "models":{
                "gpt-4o":{"id":"gpt-4o","name":"GPT-4o"},
                "gpt-3.5-turbo":{"id":"gpt-3.5-turbo","name":"GPT-3.5 Turbo"}
              }
            }
          ],
          "default":{"providerId":"openai","modelId":"gpt-4o"},
          "connected":["openai"]
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeProvidersResponse>(json, Options);

        result.ShouldNotBeNull();
        result.All.Count.ShouldBe(1);
        result.All[0].Id.ShouldBe("openai");
        result.All[0].Models.Count.ShouldBe(2);
        result.Default!.ModelId.ShouldBe("gpt-4o");
        result.Connected.ShouldContain("openai");
    }

    // ---------------------------------------------------------------------------
    // OpenCodeModelRef — null / missing field tolerance (bug fix)
    // ---------------------------------------------------------------------------

    [Fact]
    public void ModelRef_Deserializes_WithMissingFields()
    {
        // "model": {} — both providerId and modelId are absent
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>("{}", Options);

        result.ShouldNotBeNull();
        result.ProviderId.ShouldBeNull();
        result.ModelId.ShouldBeNull();
    }

    [Fact]
    public void ModelRef_Deserializes_WithNullFields()
    {
        // "model": {"providerId":null,"modelId":null}
        const string json = """{"providerId":null,"modelId":null}""";
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>(json, Options);

        result.ShouldNotBeNull();
        result.ProviderId.ShouldBeNull();
        result.ModelId.ShouldBeNull();
    }

    [Fact]
    public void ModelRef_Deserializes_WithPartialFields()
    {
        // "model": {"providerId":"openai"} — modelId absent
        const string json = """{"providerId":"openai"}""";
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>(json, Options);

        result.ShouldNotBeNull();
        result.ProviderId.ShouldBe("openai");
        result.ModelId.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------
    // Regression: missing / unknown type discriminators must NOT throw
    // ---------------------------------------------------------------------------

    [Fact]
    public void MessageInfo_MissingRoleDiscriminator_DeserializesAsBaseType()
    {
        // When OpenCode emits a message without a "role" field, the deserializer
        // must fall back to the base OpenCodeMessageInfo instead of throwing
        // NotSupportedException. This was the root cause of the "Failed to load
        // initial messages" bug.
        const string json = """
        {
          "id":"msg-no-role",
          "sessionId":"sess-1",
          "time":{"created":1000000}
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageInfo>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<OpenCodeUserMessage>();
        result.ShouldNotBeOfType<OpenCodeAssistantMessage>();
        result.Role.ShouldBe("unknown");
        result.Id.ShouldBe("msg-no-role");
    }

    [Fact]
    public void MessageInfo_UnknownRoleDiscriminator_DeserializesAsBaseType()
    {
        // A future role value (e.g. "system") must also fall back gracefully.
        const string json = """
        {
          "role":"system",
          "id":"msg-system",
          "sessionId":"sess-1",
          "time":{"created":1000000}
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageInfo>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<OpenCodeUserMessage>();
        result.ShouldNotBeOfType<OpenCodeAssistantMessage>();
        result.Role.ShouldBe("unknown");
    }

    [Fact]
    public void MessageWithParts_MissingRoleDiscriminator_DeserializesGracefully()
    {
        // The full envelope: OpenCodeMessageWithParts whose "info" lacks "role".
        // This is the exact payload shape returned by the messages endpoint.
        const string json = """
        [{
          "info": {
            "id": "msg-missing-role",
            "sessionId": "sess-1",
            "time": { "created": 1000000 }
          },
          "parts": [{"type":"text","id":"p1","sessionId":"sess-1","messageId":"msg-missing-role","text":"hi"}]
        }]
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageWithParts[]>(json, Options);

        result.ShouldNotBeNull();
        result.Length.ShouldBe(1);
        result[0].Info.Role.ShouldBe("unknown");
        result[0].Parts.Count.ShouldBe(1);
    }

    [Fact]
    public void MessagePart_MissingTypeDiscriminator_DeserializesAsBaseType()
    {
        const string json = """
        {
          "id":"part-no-type",
          "sessionId":"sess-1",
          "messageId":"msg-1"
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessagePart>(json, Options);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<OpenCodeTextPart>();
        result.ShouldNotBeOfType<OpenCodeToolPart>();
    }

    [Fact]
    public void ToolState_MissingStatusDiscriminator_DeserializesAsBaseType()
    {
        var result = JsonSerializer.Deserialize<OpenCodeToolState>("{}", Options);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<OpenCodeToolPending>();
        result.ShouldNotBeOfType<OpenCodeToolRunning>();
        result.ShouldNotBeOfType<OpenCodeToolCompleted>();
        result.ShouldNotBeOfType<OpenCodeToolError>();
    }

    [Fact]
    public void SessionStatus_MissingTypeDiscriminator_DeserializesAsBaseType()
    {
        var result = JsonSerializer.Deserialize<OpenCodeSessionStatus>("{}", Options);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<OpenCodeIdleStatus>();
        result.ShouldNotBeOfType<OpenCodeBusyStatus>();
        result.ShouldNotBeOfType<OpenCodeRetryStatus>();
    }

    [Fact]
    public void UserMessage_WithEmptyModelObject_Deserializes()
    {
        // Full OpenCodeMessageWithParts where "model": {} — the exact payload that triggered the bug
        const string json = """
        {
          "info": {
            "role": "user",
            "id": "msg-bug-1",
            "sessionId": "sess-1",
            "time": { "created": 1000000 },
            "model": {}
          },
          "parts": []
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeMessageWithParts>(json, Options);

        result.ShouldNotBeNull();
        var userMsg = result.Info.ShouldBeOfType<OpenCodeUserMessage>();
        userMsg.Model.ShouldNotBeNull();
        userMsg.Model.ProviderId.ShouldBeNull();
        userMsg.Model.ModelId.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------
    // OpenCodeCommandRequest — arguments always present, model is plain string
    // ---------------------------------------------------------------------------

    [Fact]
    public void CommandRequest_Serializes_ArgumentsAlwaysPresent()
    {
        // OpenCode's CommandInput Zod schema requires "arguments" as a non-optional string.
        // Even when empty, the field must be present in the JSON payload.
        var request = new OpenCodeCommandRequest
        {
            Command = "weave-health",
            Arguments = string.Empty,
        };

        var json = JsonSerializer.Serialize(request, Options);

        json.ShouldContain("\"command\":\"weave-health\"");
        json.ShouldContain("\"arguments\":\"\"");
        // Null optional fields should be omitted
        json.ShouldNotContain("\"agent\"");
        json.ShouldNotContain("\"model\"");
        json.ShouldNotContain("\"messageID\"");
    }

    [Fact]
    public void CommandRequest_Serializes_ModelAsPlainString()
    {
        // OpenCode's CommandInput expects model as z.string().optional(),
        // not an object like the prompt endpoint.
        var request = new OpenCodeCommandRequest
        {
            Command = "test-cmd",
            Arguments = "some args",
            Model = "openai/gpt-4o",
            Agent = "build",
        };

        var json = JsonSerializer.Serialize(request, Options);

        json.ShouldContain("\"model\":\"openai/gpt-4o\"");
        json.ShouldContain("\"agent\":\"build\"");
        json.ShouldContain("\"arguments\":\"some args\"");
    }
}
