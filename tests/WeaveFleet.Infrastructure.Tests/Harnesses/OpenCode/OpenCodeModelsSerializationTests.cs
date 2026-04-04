using System.Text.Json;
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

        Assert.NotNull(result);
        Assert.True(result.Healthy);
        Assert.Equal("1.2.3", result.Version);

        var roundTripped = JsonSerializer.Serialize(result, Options);
        Assert.Contains("\"healthy\":true", roundTripped);
        Assert.Contains("\"version\":\"1.2.3\"", roundTripped);
    }

    [Fact]
    public void SessionInfo_RoundTrips_WithNulls()
    {
        const string json = """{"id":"sess-1","version":"1","time":{"created":1000000,"updated":2000000}}""";
        var result = JsonSerializer.Deserialize<OpenCodeSessionInfo>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("sess-1", result.Id);
        Assert.Equal("1", result.Version);
        Assert.Null(result.Title);
        Assert.Equal(1000000L, result.Time!.Created);
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

        Assert.NotNull(result);
        Assert.Equal("sess-2", result.Id);
        Assert.Equal("my-session", result.Slug);
        Assert.Equal("My Session", result.Title);
        Assert.Equal("sess-0", result.ParentId);
        Assert.Equal(1500000L, result.Time!.Compacting);
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

        Assert.NotNull(result);
        Assert.IsType<OpenCodeUserMessage>(result);
        var user = (OpenCodeUserMessage)result;
        Assert.Equal("msg-1", user.Id);
        Assert.Equal("user", user.Role);
        Assert.Equal("default", user.Agent);
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

        Assert.NotNull(result);
        Assert.IsType<OpenCodeAssistantMessage>(result);
        var assistant = (OpenCodeAssistantMessage)result;
        Assert.Equal("gpt-4o", assistant.ModelId);
        Assert.Equal("openai", assistant.ProviderId);
        Assert.Equal("end_turn", assistant.Finish);
        Assert.Equal(0.001, assistant.Cost);
        Assert.Equal(2500000L, assistant.Time.Completed);
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

        Assert.NotNull(result);
        Assert.IsType<OpenCodeTextPart>(result);
        Assert.Equal("Hello world", ((OpenCodeTextPart)result).Text);
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

        Assert.NotNull(result);
        Assert.IsType<OpenCodeToolPart>(result);
        var tool = (OpenCodeToolPart)result;
        Assert.Equal("bash", tool.Tool);
        Assert.Equal("call-1", tool.CallId);
        Assert.IsType<OpenCodeToolCompleted>(tool.State);
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
            Assert.NotNull(result);
            Assert.IsType(expectedType, result);
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

        Assert.Contains("\"parts\"", json);
        Assert.Contains("\"type\":\"text\"", json);
        Assert.Contains("\"text\":\"Hello\"", json);
        Assert.Contains("\"agent\":\"default\"", json);
        // Null fields should be omitted
        Assert.DoesNotContain("\"model\"", json);
    }

    [Fact]
    public void SseEvent_Deserializes()
    {
        const string json = """{"type":"session.status","properties":{"sessionId":"sess-1","status":"idle"}}""";

        var result = JsonSerializer.Deserialize<OpenCodeSseEvent>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("session.status", result.Type);
        Assert.Equal(JsonValueKind.Object, result.Properties.ValueKind);
    }

    [Fact]
    public void SessionStatus_Discriminated_Deserializes()
    {
        var idle = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"idle"}""", Options);
        var busy = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"busy","since":1000000}""", Options);
        var retry = JsonSerializer.Deserialize<OpenCodeSessionStatus>("""{"type":"retry","reason":"rate limit","delay":2000,"count":1}""", Options);

        Assert.IsType<OpenCodeIdleStatus>(idle);
        Assert.IsType<OpenCodeBusyStatus>(busy);
        Assert.Equal(1000000L, ((OpenCodeBusyStatus)busy!).Since);
        Assert.IsType<OpenCodeRetryStatus>(retry);
        Assert.Equal("rate limit", ((OpenCodeRetryStatus)retry!).Reason);
        Assert.Equal(2000, ((OpenCodeRetryStatus)retry!).Delay);
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
              "models":[
                {"id":"gpt-4o","name":"GPT-4o"},
                {"id":"gpt-3.5-turbo","name":"GPT-3.5 Turbo"}
              ]
            }
          ],
          "default":{"providerId":"openai","modelId":"gpt-4o"},
          "connected":["openai"]
        }
        """;

        var result = JsonSerializer.Deserialize<OpenCodeProvidersResponse>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.All);
        Assert.Equal("openai", result.All[0].Id);
        Assert.Equal(2, result.All[0].Models.Count);
        Assert.Equal("gpt-4o", result.Default!.ModelId);
        Assert.Contains("openai", result.Connected);
    }

    // ---------------------------------------------------------------------------
    // OpenCodeModelRef — null / missing field tolerance (bug fix)
    // ---------------------------------------------------------------------------

    [Fact]
    public void ModelRef_Deserializes_WithMissingFields()
    {
        // "model": {} — both providerId and modelId are absent
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>("{}", Options);

        Assert.NotNull(result);
        Assert.Null(result.ProviderId);
        Assert.Null(result.ModelId);
    }

    [Fact]
    public void ModelRef_Deserializes_WithNullFields()
    {
        // "model": {"providerId":null,"modelId":null}
        const string json = """{"providerId":null,"modelId":null}""";
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>(json, Options);

        Assert.NotNull(result);
        Assert.Null(result.ProviderId);
        Assert.Null(result.ModelId);
    }

    [Fact]
    public void ModelRef_Deserializes_WithPartialFields()
    {
        // "model": {"providerId":"openai"} — modelId absent
        const string json = """{"providerId":"openai"}""";
        var result = JsonSerializer.Deserialize<OpenCodeModelRef>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("openai", result.ProviderId);
        Assert.Null(result.ModelId);
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

        Assert.NotNull(result);
        var userMsg = Assert.IsType<OpenCodeUserMessage>(result.Info);
        Assert.NotNull(userMsg.Model);
        Assert.Null(userMsg.Model.ProviderId);
        Assert.Null(userMsg.Model.ModelId);
    }
}
