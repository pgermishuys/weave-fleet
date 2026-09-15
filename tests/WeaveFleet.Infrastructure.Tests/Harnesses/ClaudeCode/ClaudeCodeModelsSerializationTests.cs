using System.Text.Json;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeModelsSerializationTests
{
    private static readonly JsonSerializerOptions Options = ClaudeCodeJsonOptions.Default;

    [Fact]
    public void SystemInitMessage_Deserializes()
    {
        const string json = """
        {
          "type": "system",
          "subtype": "init",
          "session_id": "sess-abc123",
          "model": "claude-opus-4-5",
          "tools": [{"name":"Read"}],
          "mcp_servers": []
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeSystemMessage>();
        msg.Subtype.ShouldBe("init");
        msg.SessionId.ShouldBe("sess-abc123");
        msg.Model.ShouldBe("claude-opus-4-5");
        msg.Tools.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void AssistantMessage_WithTextContent_Deserializes()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "id": "msg-001",
            "role": "assistant",
            "content": [
              { "type": "text", "text": "Hello, world!" }
            ],
            "stop_reason": "end_turn",
            "usage": { "input_tokens": 10, "output_tokens": 5 }
          }
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeAssistantMessage>();
        msg.Message.ShouldNotBeNull();
        msg.Message.Id.ShouldBe("msg-001");
        msg.Message.StopReason.ShouldBe("end_turn");
        msg.Message.Content.ShouldNotBeNull();
        msg.Message.Content.Count.ShouldBe(1);
        var textBlock = msg.Message.Content[0].ShouldBeOfType<ClaudeCodeTextBlock>();
        textBlock.Text.ShouldBe("Hello, world!");
    }

    [Fact]
    public void AssistantMessage_WithToolUse_Deserializes()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "id": "msg-002",
            "role": "assistant",
            "content": [
              {
                "type": "tool_use",
                "id": "toolu_xyz",
                "name": "Edit",
                "input": { "path": "/foo/bar.cs", "content": "new content" }
              }
            ],
            "stop_reason": "tool_use",
            "usage": { "input_tokens": 100, "output_tokens": 50 }
          }
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeAssistantMessage>();
        msg.Message?.Content.ShouldNotBeNull();
        var content = msg.Message!.Content!;
        content.Count.ShouldBe(1);
        var toolBlock = content[0].ShouldBeOfType<ClaudeCodeToolUseBlock>();
        toolBlock.Id.ShouldBe("toolu_xyz");
        toolBlock.Name.ShouldBe("Edit");
        toolBlock.Input.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void AssistantMessage_WithToolResult_Deserializes()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "id": "msg-003",
            "role": "assistant",
            "content": [
              {
                "type": "tool_result",
                "tool_use_id": "toolu_xyz",
                "content": "File written successfully.",
                "is_error": false
              }
            ],
            "stop_reason": "end_turn",
            "usage": { "input_tokens": 50, "output_tokens": 10 }
          }
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeAssistantMessage>();
        msg.Message?.Content.ShouldNotBeNull();
        var content = msg.Message!.Content!;
        content.Count.ShouldBe(1);
        var resultBlock = content[0].ShouldBeOfType<ClaudeCodeToolResultBlock>();
        resultBlock.ToolUseId.ShouldBe("toolu_xyz");
        resultBlock.Content.ShouldBe("File written successfully.");
        resultBlock.IsError.ShouldBe((bool?)false);
    }

    [Fact]
    public void AssistantMessage_WithMixedContent_Deserializes()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "id": "msg-004",
            "role": "assistant",
            "content": [
              { "type": "text", "text": "I will edit the file." },
              { "type": "tool_use", "id": "toolu_001", "name": "Write", "input": {} }
            ],
            "stop_reason": "tool_use",
            "usage": { "input_tokens": 30, "output_tokens": 20 }
          }
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeAssistantMessage>();
        msg.Message?.Content.ShouldNotBeNull();
        var content = msg.Message!.Content!;
        content.Count.ShouldBe(2);
        content[0].ShouldBeOfType<ClaudeCodeTextBlock>();
        content[1].ShouldBeOfType<ClaudeCodeToolUseBlock>();
    }

    [Fact]
    public void ResultMessage_Success_Deserializes()
    {
        const string json = """
        {
          "type": "result",
          "subtype": "success",
          "result": "Task completed.",
          "duration_ms": 5432,
          "num_turns": 3,
          "total_cost_usd": 0.042,
          "session_id": "sess-abc123",
          "usage": { "input_tokens": 500, "output_tokens": 200 }
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeResultMessage>();
        msg.Subtype.ShouldBe("success");
        msg.Result.ShouldBe("Task completed.");
        msg.DurationMs.ShouldBe(5432L);
        msg.NumTurns.ShouldBe(3);
        msg.TotalCostUsd.ShouldBe(0.042m);
        msg.SessionId.ShouldBe("sess-abc123");
        msg.Usage.ShouldNotBeNull();
        msg.Usage.InputTokens.ShouldBe(500);
        msg.Usage.OutputTokens.ShouldBe(200);
    }

    [Fact]
    public void ResultMessage_ErrorMaxTurns_Deserializes()
    {
        const string json = """
        {
          "type": "result",
          "subtype": "error_max_turns",
          "result": null,
          "num_turns": 10,
          "session_id": "sess-xyz"
        }
        """;

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        var msg = result.ShouldBeOfType<ClaudeCodeResultMessage>();
        msg.Subtype.ShouldBe("error_max_turns");
        msg.NumTurns.ShouldBe(10);
        msg.SessionId.ShouldBe("sess-xyz");
        msg.Result.ShouldBeNull();
    }

    [Fact]
    public void Usage_Deserializes_WithAllFields()
    {
        const string json = """
        {
          "input_tokens": 100,
          "output_tokens": 50,
          "cache_read_input_tokens": 20,
          "cache_creation_input_tokens": 10
        }
        """;

        var usage = JsonSerializer.Deserialize<ClaudeCodeUsage>(json, Options);

        usage.ShouldNotBeNull();
        usage.InputTokens.ShouldBe(100);
        usage.OutputTokens.ShouldBe(50);
        usage.CacheReadInputTokens.ShouldBe(20);
        usage.CacheCreationInputTokens.ShouldBe(10);
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_TextBlock()
    {
        const string json = """{ "type": "text", "text": "Some text" }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var textBlock = block.ShouldBeOfType<ClaudeCodeTextBlock>();
        textBlock.Text.ShouldBe("Some text");
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_ToolUseBlock()
    {
        const string json = """{ "type": "tool_use", "id": "toolu_1", "name": "Bash", "input": {"cmd":"ls"} }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var toolUse = block.ShouldBeOfType<ClaudeCodeToolUseBlock>();
        toolUse.Id.ShouldBe("toolu_1");
        toolUse.Name.ShouldBe("Bash");
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_ToolResultBlock()
    {
        const string json = """{ "type": "tool_result", "tool_use_id": "toolu_1", "content": "ok", "is_error": false }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var toolResult = block.ShouldBeOfType<ClaudeCodeToolResultBlock>();
        toolResult.ToolUseId.ShouldBe("toolu_1");
        toolResult.Content.ShouldBe("ok");
        toolResult.IsError.ShouldBe((bool?)false);
    }

    [Fact]
    public void StreamMessage_UnknownType_DeserializesAsBaseType()
    {
        // A future message type we don't know about should fall back to base, not throw
        const string json = """{ "type": "heartbeat", "interval_ms": 1000 }""";

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        // Should not throw — falls back to base type
        result.ShouldBeOfType<ClaudeCodeStreamMessage>();
    }

    [Fact]
    public void ContentBlock_UnknownType_DeserializesAsBaseType()
    {
        const string json = """{ "type": "image", "source": {"type":"base64","media_type":"image/png","data":"abc"} }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        block.ShouldBeOfType<ClaudeCodeContentBlock>();
    }

    [Fact]
    public void ResultMessage_TypeNotFirst_Deserializes()
    {
        // Claude Code writes result lines with "type" near the end.
        const string json = """
        {"duration_api_ms":7070,"session_id":"sess-1","total_cost_usd":0.01,"is_error":true,"num_turns":2,
         "subtype":"error_max_turns","errors":["Reached maximum number of turns (1)"],"type":"result"}
        """;

        var result = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage);

        var msg = result.ShouldBeOfType<ClaudeCodeResultMessage>();
        msg.IsError.ShouldBe(true);
        msg.Subtype.ShouldBe("error_max_turns");
        msg.Errors.ShouldBe(["Reached maximum number of turns (1)"]);
    }

    [Fact]
    public void UserMessage_WithToolResult_Deserializes()
    {
        const string json = """
        {"type":"user","message":{"role":"user","content":[
          {"tool_use_id":"toolu_1","type":"tool_result","content":"done","is_error":false}]},
         "parent_tool_use_id":null,"session_id":"sess-1"}
        """;

        var result = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage);

        var msg = result.ShouldBeOfType<ClaudeCodeUserMessage>();
        msg.ParentToolUseId.ShouldBeNull();
        var block = msg.Message!.Content!.ShouldHaveSingleItem().ShouldBeOfType<ClaudeCodeToolResultBlock>();
        block.ToolUseId.ShouldBe("toolu_1");
        block.Content.ShouldBe("done");
    }

    [Fact]
    public void ToolResultBlock_ContentAsBlocks_ReadsAsText()
    {
        // MCP tools and images return a list of content blocks instead of a string.
        const string json = """
        {"type":"user","message":{"role":"user","content":[
          {"tool_use_id":"toolu_1","type":"tool_result","content":[
            {"type":"text","text":"first"},{"type":"image","source":{"type":"base64","data":"abc"}},{"type":"text","text":"second"}]}]}}
        """;

        var result = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage);

        var msg = result.ShouldBeOfType<ClaudeCodeUserMessage>();
        msg.Message!.Content!.ShouldHaveSingleItem().ShouldBeOfType<ClaudeCodeToolResultBlock>()
            .Content.ShouldBe("first\n[image]\nsecond");
    }

    [Fact]
    public void ThinkingBlock_Deserializes()
    {
        const string json = """{ "type": "thinking", "thinking": "Let me look.", "signature": "abc" }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        block.ShouldBeOfType<ClaudeCodeThinkingBlock>().Thinking.ShouldBe("Let me look.");
    }

    [Fact]
    public void StreamMessage_MissingTypeDiscriminator_DeserializesAsBaseType()
    {
        const string json = """{ "some_field": "some_value" }""";

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        result.ShouldBeOfType<ClaudeCodeStreamMessage>();
    }
}
