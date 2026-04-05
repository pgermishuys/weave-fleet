using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeMapperTests
{
    // -----------------------------------------------------------------------
    // ToHarnessMessage
    // -----------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_MapsTextContent_ToTextPart()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-001",
                Role = "assistant",
                Content = [new ClaudeCodeTextBlock { Text = "Hello, world!" }],
                StopReason = "end_turn",
            },
        };
        var ts = DateTimeOffset.UtcNow;

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, ts);

        Assert.Equal("msg-001", result.Id);
        Assert.Equal("assistant", result.Role);
        Assert.Equal(ts, result.Timestamp);
        var part = Assert.IsType<TextPart>(Assert.Single(result.Parts));
        Assert.Equal("Hello, world!", part.Text);
    }

    [Fact]
    public void ToHarnessMessage_MapsToolUse_ToToolUsePart()
    {
        var inputJson = JsonDocument.Parse("""{"path":"/foo/bar.cs","content":"x"}""").RootElement;
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-002",
                Content = [new ClaudeCodeToolUseBlock { Id = "toolu_abc", Name = "Edit", Input = inputJson }],
            },
        };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        var part = Assert.IsType<ToolUsePart>(Assert.Single(result.Parts));
        Assert.Equal("toolu_abc", part.ToolCallId);
        Assert.Equal("Edit", part.ToolName);
        Assert.Equal(ToolUseState.Running, part.State);
    }

    [Fact]
    public void ToHarnessMessage_MapsToolResult_ToToolResultPart()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-003",
                Content = [new ClaudeCodeToolResultBlock
                {
                    ToolUseId = "toolu_abc",
                    Content = "File written.",
                    IsError = false,
                }],
            },
        };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        var part = Assert.IsType<ToolResultPart>(Assert.Single(result.Parts));
        Assert.Equal("toolu_abc", part.ToolCallId);
        Assert.Equal("File written.", part.Content);
        Assert.False(part.IsError);
    }

    [Fact]
    public void ToHarnessMessage_MapsMixedContent_ToMultipleParts()
    {
        var inputJson = JsonDocument.Parse("{}").RootElement;
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-004",
                Content =
                [
                    new ClaudeCodeTextBlock { Text = "I will edit the file." },
                    new ClaudeCodeToolUseBlock { Id = "toolu_1", Name = "Write", Input = inputJson },
                ],
            },
        };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        Assert.Equal(2, result.Parts.Count);
        Assert.IsType<TextPart>(result.Parts[0]);
        Assert.IsType<ToolUsePart>(result.Parts[1]);
    }

    [Fact]
    public void ToHarnessMessage_NullContent_ReturnsEmptyParts()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage { Id = "msg-005", Content = null },
        };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        Assert.Empty(result.Parts);
    }

    [Fact]
    public void ToHarnessMessage_NullMessageId_GeneratesFallbackId()
    {
        var msg = new ClaudeCodeAssistantMessage { Message = null };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        Assert.False(string.IsNullOrEmpty(result.Id));
        Assert.StartsWith("assistant-", result.Id);
    }

    // -----------------------------------------------------------------------
    // ToUserMessage
    // -----------------------------------------------------------------------

    [Fact]
    public void ToUserMessage_CreatesValidUserMessage()
    {
        var ts = DateTimeOffset.UtcNow;

        var result = ClaudeCodeMapper.ToUserMessage("Fix the bug.", ts);

        Assert.Equal("user", result.Role);
        Assert.Equal(ts, result.Timestamp);
        Assert.StartsWith("user-", result.Id);
        var part = Assert.IsType<TextPart>(Assert.Single(result.Parts));
        Assert.Equal("Fix the bug.", part.Text);
    }

    // -----------------------------------------------------------------------
    // ToFrontendEvents
    // -----------------------------------------------------------------------

    [Fact]
    public void ToFrontendEvents_AssistantMessage_ProducesMessageUpdatedAndPartEvents()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-1",
                Role = "assistant",
                Content = [new ClaudeCodeTextBlock { Text = "Hello!" }],
            },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        Assert.Equal(2, events.Count);
        Assert.Equal("message.updated", events[0].Type);
        Assert.Equal("message.part.updated", events[1].Type);
        Assert.Equal("fleet-sess-1", events[0].SessionId);
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessage_MessageUpdatedPayloadHasInfoWithId()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-1",
                Content = [new ClaudeCodeTextBlock { Text = "Hi" }],
            },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        var payload = events[0].Payload!.Value;
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        var info = payload.GetProperty("info");
        Assert.Equal("msg-1", info.GetProperty("id").GetString());
        Assert.Equal("assistant", info.GetProperty("role").GetString());
        Assert.Equal("fleet-sess-1", info.GetProperty("sessionID").GetString());
        Assert.True(info.GetProperty("time").GetProperty("created").GetInt64() > 0);
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessage_PartUpdatedPayloadHasTextContent()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-1",
                Content = [new ClaudeCodeTextBlock { Text = "Hello, world!" }],
            },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        var partPayload = events[1].Payload!.Value.GetProperty("part");
        Assert.Equal("text", partPayload.GetProperty("type").GetString());
        Assert.Equal("Hello, world!", partPayload.GetProperty("text").GetString());
        Assert.Equal("msg-1", partPayload.GetProperty("messageID").GetString());
        Assert.Equal("fleet-sess-1", partPayload.GetProperty("sessionID").GetString());
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessageWithTool_ProducesToolPartEvent()
    {
        var inputJson = JsonDocument.Parse("""{"path":"/foo"}""").RootElement;
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-2",
                Content =
                [
                    new ClaudeCodeTextBlock { Text = "Editing..." },
                    new ClaudeCodeToolUseBlock { Id = "toolu_1", Name = "Edit", Input = inputJson },
                ],
            },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        // 1 message.updated + 2 message.part.updated (text + tool)
        Assert.Equal(3, events.Count);
        Assert.Equal("message.updated", events[0].Type);
        Assert.Equal("message.part.updated", events[1].Type);
        Assert.Equal("message.part.updated", events[2].Type);

        var toolPart = events[2].Payload!.Value.GetProperty("part");
        Assert.Equal("tool", toolPart.GetProperty("type").GetString());
        Assert.Equal("Edit", toolPart.GetProperty("tool").GetString());
        Assert.Equal("toolu_1", toolPart.GetProperty("callID").GetString());
        Assert.Equal("running", toolPart.GetProperty("state").GetProperty("status").GetString());
    }

    [Fact]
    public void ToFrontendEvents_ResultMessage_ProducesSessionIdleEvent()
    {
        var msg = new ClaudeCodeResultMessage { Subtype = "success", SessionId = "sess-1" };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        var evt = Assert.Single(events);
        Assert.Equal("session.idle", evt.Type);
        Assert.Equal("fleet-sess-1", evt.SessionId);
    }

    [Fact]
    public void ToFrontendEvents_SystemInitMessage_ProducesNoEvents()
    {
        var msg = new ClaudeCodeSystemMessage { Subtype = "init", SessionId = "sess-1" };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        Assert.Empty(events);
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessageNullContent_ProducesOnlyMessageUpdated()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage { Id = "msg-empty", Content = null },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        var evt = Assert.Single(events);
        Assert.Equal("message.updated", evt.Type);
    }

    // -----------------------------------------------------------------------
    // CreateSessionStatusEvent
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateSessionStatusEvent_BusyStatus_HasCorrectPayload()
    {
        var evt = ClaudeCodeMapper.CreateSessionStatusEvent("fleet-sess-1", "busy");

        Assert.Equal("session.status", evt.Type);
        Assert.Equal("fleet-sess-1", evt.SessionId);
        var payload = evt.Payload!.Value;
        Assert.Equal("busy", payload.GetProperty("status").GetProperty("type").GetString());
    }

    // -----------------------------------------------------------------------
    // ToMessagePart
    // -----------------------------------------------------------------------

    [Fact]
    public void ToMessagePart_UnknownBlockType_ReturnsNull()
    {
        // Base ClaudeCodeContentBlock (unrecognized subtype) → null
        var block = new ClaudeCodeContentBlock();

        var result = ClaudeCodeMapper.ToMessagePart(block);

        Assert.Null(result);
    }

    [Fact]
    public void ToMessagePart_TextBlock_NullText_ReturnsNull()
    {
        var block = new ClaudeCodeTextBlock { Text = null };

        var result = ClaudeCodeMapper.ToMessagePart(block);

        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // TryExtractTokenEvent
    // -----------------------------------------------------------------------

    [Fact]
    public void TryExtractTokenEvent_SuccessResult_ReturnsData()
    {
        var result = new ClaudeCodeResultMessage
        {
            Subtype = "success",
            SessionId = "sess-1",
            TotalCostUsd = 0.042m,
            Usage = new ClaudeCodeUsage
            {
                InputTokens = 500,
                OutputTokens = 200,
                CacheReadInputTokens = 10,
                CacheCreationInputTokens = 5,
            },
        };

        var data = ClaudeCodeMapper.TryExtractTokenEvent(
            result,
            sessionId: "sess-1",
            projectId: "proj-1",
            projectName: "My Project",
            workspaceDirectory: "/home/user/project",
            modelId: "claude-opus-4-5");

        Assert.NotNull(data);
        Assert.Equal("sess-1", data.SessionId);
        Assert.Equal("proj-1", data.ProjectId);
        Assert.Equal("My Project", data.ProjectName);
        Assert.Equal("anthropic", data.ProviderId);
        Assert.Equal(500.0, data.TokensInput);
        Assert.Equal(200.0, data.TokensOutput);
        Assert.Equal(10.0, data.TokensCacheRead);
        Assert.Equal(5.0, data.TokensCacheWrite);
        Assert.Equal(0.042, data.Cost, precision: 6);
    }

    [Fact]
    public void TryExtractTokenEvent_NoUsageOrCost_ReturnsNull()
    {
        var result = new ClaudeCodeResultMessage
        {
            Subtype = "success",
            SessionId = "sess-2",
            Usage = null,
            TotalCostUsd = null,
        };

        var data = ClaudeCodeMapper.TryExtractTokenEvent(
            result, "sess-2", null, null, null, null);

        Assert.Null(data);
    }

    [Fact]
    public void TryExtractTokenEvent_UsageWithoutCost_ReturnsData()
    {
        // Usage is set but TotalCostUsd is null — should still return data
        var result = new ClaudeCodeResultMessage
        {
            Subtype = "success",
            SessionId = "sess-3",
            TotalCostUsd = null,
            Usage = new ClaudeCodeUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var data = ClaudeCodeMapper.TryExtractTokenEvent(
            result, "sess-3", null, null, null, "claude-haiku-3-5");

        Assert.NotNull(data);
        Assert.Equal(100.0, data.TokensInput);
        Assert.Equal(50.0, data.TokensOutput);
        Assert.Equal(0.0, data.Cost);
    }

    [Fact]
    public void TryExtractTokenEvent_UsesResultSessionIdOverParameter()
    {
        var result = new ClaudeCodeResultMessage
        {
            Subtype = "success",
            SessionId = "claude-native-sess",
            TotalCostUsd = 0.01m,
            Usage = new ClaudeCodeUsage { InputTokens = 10, OutputTokens = 5 },
        };

        var data = ClaudeCodeMapper.TryExtractTokenEvent(
            result, "fleet-sess-fallback", null, null, null, null);

        Assert.NotNull(data);
        // result.SessionId takes precedence
        Assert.Equal("claude-native-sess", data.SessionId);
    }
}
