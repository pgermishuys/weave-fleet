using System.Text.Json;
using Shouldly;
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

        result.Id.ShouldBe("msg-001");
        result.Role.ShouldBe("assistant");
        result.Timestamp.ShouldBe(ts);
        result.Parts.Count.ShouldBe(1);
        var part = result.Parts[0].ShouldBeOfType<TextPart>();
        part.Text.ShouldBe("Hello, world!");
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

        result.Parts.Count.ShouldBe(1);
        var part = result.Parts[0].ShouldBeOfType<ToolUsePart>();
        part.ToolCallId.ShouldBe("toolu_abc");
        part.ToolName.ShouldBe("Edit");
        part.State.ShouldBe(ToolUseState.Running);
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

        result.Parts.Count.ShouldBe(1);
        var part = result.Parts[0].ShouldBeOfType<ToolResultPart>();
        part.ToolCallId.ShouldBe("toolu_abc");
        part.Content.ShouldBe("File written.");
        part.IsError.ShouldBeFalse();
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

        result.Parts.Count.ShouldBe(2);
        result.Parts[0].ShouldBeOfType<TextPart>();
        result.Parts[1].ShouldBeOfType<ToolUsePart>();
    }

    [Fact]
    public void ToHarnessMessage_NullContent_ReturnsEmptyParts()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage { Id = "msg-005", Content = null },
        };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        result.Parts.ShouldBeEmpty();
    }

    [Fact]
    public void ToHarnessMessage_NullMessageId_GeneratesFallbackId()
    {
        var msg = new ClaudeCodeAssistantMessage { Message = null };

        var result = ClaudeCodeMapper.ToHarnessMessage(msg, DateTimeOffset.UtcNow);

        result.Id.ShouldNotBeNullOrEmpty();
        result.Id.ShouldStartWith("assistant-");
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

        events.Count.ShouldBe(2);
        events[0].Type.ShouldBe("message.updated");
        events[1].Type.ShouldBe("message.part.updated");
        events[0].SessionId.ShouldBe("fleet-sess-1");
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
        payload.ValueKind.ShouldBe(JsonValueKind.Object);
        var info = payload.GetProperty("info");
        info.GetProperty("id").GetString().ShouldBe("msg-1");
        info.GetProperty("role").GetString().ShouldBe("assistant");
        info.GetProperty("sessionID").GetString().ShouldBe("fleet-sess-1");
        info.GetProperty("time").GetProperty("created").GetInt64().ShouldBeGreaterThan(0);
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
        partPayload.GetProperty("type").GetString().ShouldBe("text");
        partPayload.GetProperty("text").GetString().ShouldBe("Hello, world!");
        partPayload.GetProperty("messageID").GetString().ShouldBe("msg-1");
        partPayload.GetProperty("sessionID").GetString().ShouldBe("fleet-sess-1");
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
        events.Count.ShouldBe(3);
        events[0].Type.ShouldBe("message.updated");
        events[1].Type.ShouldBe("message.part.updated");
        events[2].Type.ShouldBe("message.part.updated");

        var toolPart = events[2].Payload!.Value.GetProperty("part");
        toolPart.GetProperty("type").GetString().ShouldBe("tool");
        toolPart.GetProperty("tool").GetString().ShouldBe("Edit");
        toolPart.GetProperty("callID").GetString().ShouldBe("toolu_1");
        toolPart.GetProperty("state").GetProperty("status").GetString().ShouldBe("running");
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessageWithToolResult_DoesNotEmitToolResultPartEvent()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage
            {
                Id = "msg-tool-result-1",
                Content =
                [
                    new ClaudeCodeTextBlock { Text = "Done" },
                    new ClaudeCodeToolResultBlock { ToolUseId = "toolu_1", Content = "sensitive output", IsError = false },
                ],
            },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        events.Count.ShouldBe(2);
        events.All(evt => evt.Payload?.ToString()?.Contains("sensitive output", StringComparison.Ordinal) != true).ShouldBeTrue();
    }

    [Fact]
    public void ToFrontendEvents_ResultMessage_ProducesSessionIdleEvent()
    {
        var msg = new ClaudeCodeResultMessage { Subtype = "success", SessionId = "sess-1" };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        events.Count.ShouldBe(1);
        var evt = events[0];
        evt.Type.ShouldBe("session.idle");
        evt.SessionId.ShouldBe("fleet-sess-1");
    }

    [Fact]
    public void ToFrontendEvents_SystemInitMessage_ProducesNoEvents()
    {
        var msg = new ClaudeCodeSystemMessage { Subtype = "init", SessionId = "sess-1" };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        events.ShouldBeEmpty();
    }

    [Fact]
    public void ToFrontendEvents_AssistantMessageNullContent_ProducesOnlyMessageUpdated()
    {
        var msg = new ClaudeCodeAssistantMessage
        {
            Message = new ClaudeCodeApiMessage { Id = "msg-empty", Content = null },
        };

        var events = ClaudeCodeMapper.ToFrontendEvents(msg, "fleet-sess-1");

        events.Count.ShouldBe(1);
        var evt = events[0];
        evt.Type.ShouldBe("message.updated");
    }

    // -----------------------------------------------------------------------
    // CreateSessionStatusEvent
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateSessionStatusEvent_BusyStatus_HasCorrectPayload()
    {
        var evt = ClaudeCodeMapper.CreateSessionStatusEvent("fleet-sess-1", "busy");

        evt.Type.ShouldBe("session.status");
        evt.SessionId.ShouldBe("fleet-sess-1");
        var payload = evt.Payload!.Value;
        payload.GetProperty("status").GetProperty("type").GetString().ShouldBe("busy");
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

        result.ShouldBeNull();
    }

    [Fact]
    public void ToMessagePart_TextBlock_NullText_ReturnsNull()
    {
        var block = new ClaudeCodeTextBlock { Text = null };

        var result = ClaudeCodeMapper.ToMessagePart(block);

        result.ShouldBeNull();
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

        data.ShouldNotBeNull();
        data.SessionId.ShouldBe("sess-1");
        data.ProjectId.ShouldBe("proj-1");
        data.ProjectName.ShouldBe("My Project");
        data.ProviderId.ShouldBe("anthropic");
        data.TokensInput.ShouldBe(500.0);
        data.TokensOutput.ShouldBe(200.0);
        data.TokensCacheRead.ShouldBe(10.0);
        data.TokensCacheWrite.ShouldBe(5.0);
        data.Cost.ShouldBe(0.042, 0.000001);
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

        data.ShouldBeNull();
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

        data.ShouldNotBeNull();
        data.TokensInput.ShouldBe(100.0);
        data.TokensOutput.ShouldBe(50.0);
        data.Cost.ShouldBe(0.0);
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

        data.ShouldNotBeNull();
        // result.SessionId takes precedence
        data.SessionId.ShouldBe("claude-native-sess");
    }
}
