using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeMapperTests
{
    // ---------------------------------------------------------------------------
    // ToHarnessMessage — UserMessage
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_UserMessage_MapsCorrectly()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage
            {
                Id = "msg-user-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 1_000_000L },
                Agent = "default",
            },
            Parts = [new OpenCodeTextPart { Text = "Hello" }],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        Assert.Equal("msg-user-1", result.Id);
        Assert.Equal("user", result.Role);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000L), result.Timestamp);
        Assert.Single(result.Parts);
        Assert.Equal("Hello", result.TextContent);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — AssistantMessage
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_AssistantMessage_MapsCorrectly()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-asst-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 2_000_000L },
                ModelId = "gpt-4o",
                ProviderId = "openai",
            },
            Parts = [],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        Assert.Equal("msg-asst-1", result.Id);
        Assert.Equal("assistant", result.Role);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2_000_000L), result.Timestamp);
        Assert.Empty(result.Parts);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — ExtractsTextFromParts
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_ExtractsTextFromParts()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-2",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeTextPart { Text = "Part one. " },
                new OpenCodeTextPart { Text = "Part two." },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        Assert.Equal(2, result.Parts.Count);
        Assert.Equal("Part one. Part two.", result.TextContent);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — ToolPart maps to ToolUsePart
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_ToolPart_MapsToToolUsePart()
    {
        var inputJson = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-3",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeToolPart
                {
                    CallId = "call-1",
                    Tool = "bash",
                    State = new OpenCodeToolCompleted { Input = inputJson },
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        Assert.Single(result.Parts);
        var toolPart = Assert.IsType<ToolUsePart>(result.Parts[0]);
        Assert.Equal("call-1", toolPart.ToolCallId);
        Assert.Equal("bash", toolPart.ToolName);
        Assert.Equal(ToolUseState.Completed, toolPart.State);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessEvent
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessEvent_MapsTypeAndPayload()
    {
        var properties = JsonDocument.Parse("""{"sessionId":"sess-42","status":"idle"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var before = DateTimeOffset.UtcNow;
        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-session");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("session.status", result.Type);
        Assert.Equal("sess-42", result.SessionId); // extracted from properties
        Assert.InRange(result.Timestamp, before, after);
        Assert.Equal(JsonValueKind.Object, result.Payload!.Value.ValueKind);
    }

    [Fact]
    public void ToHarnessEvent_FallsBackToProvidedSessionId_WhenPropertiesLackIt()
    {
        var properties = JsonDocument.Parse("""{"status":"busy"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-id");

        Assert.Equal("fallback-id", result.SessionId);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessAgents
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessAgents_MapsNameAndDescription()
    {
        var agents = new[]
        {
            new OpenCodeAgentInfo { Name = "coder", Description = "writes code", Mode = "build" },
            new OpenCodeAgentInfo { Name = "reviewer", Description = null, Mode = null },
        };

        var result = OpenCodeMapper.ToHarnessAgents(agents);

        Assert.Equal(2, result.Count);
        Assert.Equal("coder", result[0].Name);
        Assert.Equal("writes code", result[0].Description);
        Assert.Equal("build", result[0].Mode);
        Assert.Equal("reviewer", result[1].Name);
        Assert.Null(result[1].Description);
        Assert.Null(result[1].Mode);
    }

    [Fact]
    public void ToHarnessAgents_SkipsAgentsWithNullName()
    {
        // Agents with null Name are filtered out — a nameless agent cannot be referenced
        var agents = new[]
        {
            new OpenCodeAgentInfo { Name = "valid-agent", Description = "desc" },
            new OpenCodeAgentInfo { Name = null, Description = "nameless" },
        };

        var result = OpenCodeMapper.ToHarnessAgents(agents);

        Assert.Single(result);
        Assert.Equal("valid-agent", result[0].Name);
    }

    [Fact]
    public void ToHarnessMessage_UserMessage_WithNullModelRefFields_DoesNotThrow()
    {
        // Reproduces the deserialization scenario where "model": {} produces an
        // OpenCodeModelRef with both fields null. The mapper must not throw.
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage
            {
                Id = "msg-null-model",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 1_000_000L },
                Model = new OpenCodeModelRef(), // ProviderId = null, ModelId = null
            },
            Parts = [new OpenCodeTextPart { Text = "test" }],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        Assert.Equal("msg-null-model", result.Id);
        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessProviders
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessProviders_MapsProviderAndModels()
    {
        var response = new OpenCodeProvidersResponse
        {
            All =
            [
                new OpenCodeProviderInfo
                {
                    Id = "openai",
                    Name = "OpenAI",
                    Models =
                    [
                        new OpenCodeProviderModel { Id = "gpt-4o", Name = "GPT-4o" },
                        new OpenCodeProviderModel { Id = "gpt-3.5-turbo", Name = null },
                    ],
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessProviders(response);

        Assert.Single(result);
        Assert.Equal("openai", result[0].Id);
        Assert.Equal("OpenAI", result[0].Name);
        Assert.Equal(2, result[0].Models.Count);
        Assert.Equal("gpt-4o", result[0].Models[0].Id);
        Assert.Equal("GPT-4o", result[0].Models[0].Name);
        // Model with null Name falls back to Id
        Assert.Equal("gpt-3.5-turbo", result[0].Models[1].Name);
    }

    // ---------------------------------------------------------------------------
    // DateTimeOffsetFromUnixMs
    // ---------------------------------------------------------------------------

    [Fact]
    public void DateTimeOffsetFromUnixMs_ConvertsCorrectly()
    {
        // Unix epoch = 0ms → 1970-01-01 00:00:00 UTC
        var epoch = OpenCodeMapper.DateTimeOffsetFromUnixMs(0L);
        Assert.Equal(DateTimeOffset.UnixEpoch, epoch);

        // 1_000ms = 1 second after epoch
        var oneSecond = OpenCodeMapper.DateTimeOffsetFromUnixMs(1_000L);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), oneSecond);
    }

    // ---------------------------------------------------------------------------
    // TryExtractTokenEvent
    // ---------------------------------------------------------------------------

    private static OpenCodeSseEvent MakeMessageUpdatedEvent(string propertiesJson)
    {
        var properties = JsonDocument.Parse(propertiesJson).RootElement;
        return new OpenCodeSseEvent { Type = "message.updated", Properties = properties };
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessages — skips messages with missing/unknown role discriminator
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessages_SkipsMessagesWithUnknownRole()
    {
        // Regression: messages deserialized as base OpenCodeMessageInfo (Role == "unknown")
        // because the role discriminator was missing must be silently skipped rather
        // than causing a downstream mapping error.
        var msgs = new List<OpenCodeMessageWithParts>
        {
            new()
            {
                Info = new OpenCodeUserMessage
                {
                    Id = "msg-1",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 1_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "Hello" }],
            },
            new()
            {
                // Base type — missing role discriminator, Role == "unknown"
                Info = new OpenCodeMessageInfo
                {
                    Id = "msg-bad",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 2_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "ghost" }],
            },
            new()
            {
                Info = new OpenCodeAssistantMessage
                {
                    Id = "msg-2",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 3_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "World" }],
            },
        };

        var result = OpenCodeMapper.ToHarnessMessages(msgs);

        Assert.Equal(2, result.Count);
        Assert.Equal("msg-1", result[0].Id);
        Assert.Equal("msg-2", result[1].Id);
    }

    // ---------------------------------------------------------------------------
    // TryExtractTokenEvent
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExtractTokenEvent_ValidAssistantMessage_ReturnsTokenEventData()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-1",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 },
                "modelId": "claude-sonnet-4",
                "providerId": "anthropic",
                "tokens": {
                  "input": 100,
                  "output": 200,
                  "reasoning": 10,
                  "cache": { "read": 50, "write": 0 },
                  "total": 360
                },
                "cost": 0.005
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(
            evt, "fleet-sess-1", "proj-1", "MyProject", "/workspace");

        Assert.NotNull(result);
        Assert.Equal("fleet-sess-1:msg-1", result.EventId);
        Assert.Equal("fleet-sess-1", result.SessionId);
        Assert.Equal("claude-sonnet-4", result.ModelId);
        Assert.Equal("anthropic", result.ProviderId);
        Assert.Equal(100, result.TokensInput);
        Assert.Equal(200, result.TokensOutput);
        Assert.Equal(10, result.TokensReasoning);
        Assert.Equal(50, result.TokensCacheRead);
        Assert.Equal(360, result.TokensTotal);
        Assert.Equal(0.005, result.Cost);
        Assert.Equal("proj-1", result.ProjectId);
        Assert.Equal("MyProject", result.ProjectName);
    }

    [Fact]
    public void TryExtractTokenEvent_UserMessage_ReturnsNull()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-user-1",
                "sessionId": "oc-sess-1",
                "role": "user",
                "time": { "created": 1000000 }
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtractTokenEvent_NonMessageUpdatedEvent_ReturnsNull()
    {
        var properties = JsonDocument.Parse("""{"status":"idle"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtractTokenEvent_MessageCreatedEvent_ReturnsNull()
    {
        // message.created has unverified Properties structure — must be excluded
        var properties = JsonDocument.Parse("""{"info":{"id":"msg-1","role":"assistant","time":{"created":1000}}}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.created", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtractTokenEvent_AssistantMessageNoTokenData_ReturnsNull()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-no-tokens",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 }
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtractTokenEvent_PropertiesLacksInfoKey_ReturnsNull()
    {
        var properties = JsonDocument.Parse("""{"other":"value"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.updated", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryExtractTokenEvent_EstimatedCost_ComputedForKnownModel()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-cost",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 },
                "modelId": "claude-sonnet-4-20250514",
                "tokens": { "input": 1000, "output": 500, "total": 1500 },
                "cost": 0.01
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        Assert.NotNull(result);
        Assert.NotNull(result.EstimatedCost);
        Assert.True(result.EstimatedCost > 0);
    }
}
