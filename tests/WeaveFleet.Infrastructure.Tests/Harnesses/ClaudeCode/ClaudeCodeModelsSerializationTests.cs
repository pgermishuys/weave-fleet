using System.Text.Json;
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

        var msg = Assert.IsType<ClaudeCodeSystemMessage>(result);
        Assert.Equal("init", msg.Subtype);
        Assert.Equal("sess-abc123", msg.SessionId);
        Assert.Equal("claude-opus-4-5", msg.Model);
        Assert.True(msg.Tools.HasValue);
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

        var msg = Assert.IsType<ClaudeCodeAssistantMessage>(result);
        Assert.NotNull(msg.Message);
        Assert.Equal("msg-001", msg.Message.Id);
        Assert.Equal("end_turn", msg.Message.StopReason);
        Assert.NotNull(msg.Message.Content);
        var textBlock = Assert.IsType<ClaudeCodeTextBlock>(Assert.Single(msg.Message.Content));
        Assert.Equal("Hello, world!", textBlock.Text);
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

        var msg = Assert.IsType<ClaudeCodeAssistantMessage>(result);
        Assert.NotNull(msg.Message?.Content);
        var content = msg.Message!.Content!;
        var toolBlock = Assert.IsType<ClaudeCodeToolUseBlock>(Assert.Single(content));
        Assert.Equal("toolu_xyz", toolBlock.Id);
        Assert.Equal("Edit", toolBlock.Name);
        Assert.Equal(JsonValueKind.Object, toolBlock.Input.ValueKind);
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

        var msg = Assert.IsType<ClaudeCodeAssistantMessage>(result);
        Assert.NotNull(msg.Message?.Content);
        var content = msg.Message!.Content!;
        var resultBlock = Assert.IsType<ClaudeCodeToolResultBlock>(Assert.Single(content));
        Assert.Equal("toolu_xyz", resultBlock.ToolUseId);
        Assert.Equal("File written successfully.", resultBlock.Content);
        Assert.False(resultBlock.IsError);
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

        var msg = Assert.IsType<ClaudeCodeAssistantMessage>(result);
        Assert.NotNull(msg.Message?.Content);
        var content = msg.Message!.Content!;
        Assert.Equal(2, content.Count);
        Assert.IsType<ClaudeCodeTextBlock>(content[0]);
        Assert.IsType<ClaudeCodeToolUseBlock>(content[1]);
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

        var msg = Assert.IsType<ClaudeCodeResultMessage>(result);
        Assert.Equal("success", msg.Subtype);
        Assert.Equal("Task completed.", msg.Result);
        Assert.Equal(5432L, msg.DurationMs);
        Assert.Equal(3, msg.NumTurns);
        Assert.Equal(0.042m, msg.TotalCostUsd);
        Assert.Equal("sess-abc123", msg.SessionId);
        Assert.NotNull(msg.Usage);
        Assert.Equal(500, msg.Usage.InputTokens);
        Assert.Equal(200, msg.Usage.OutputTokens);
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

        var msg = Assert.IsType<ClaudeCodeResultMessage>(result);
        Assert.Equal("error_max_turns", msg.Subtype);
        Assert.Equal(10, msg.NumTurns);
        Assert.Equal("sess-xyz", msg.SessionId);
        Assert.Null(msg.Result);
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

        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
        Assert.Equal(20, usage.CacheReadInputTokens);
        Assert.Equal(10, usage.CacheCreationInputTokens);
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_TextBlock()
    {
        const string json = """{ "type": "text", "text": "Some text" }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var textBlock = Assert.IsType<ClaudeCodeTextBlock>(block);
        Assert.Equal("Some text", textBlock.Text);
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_ToolUseBlock()
    {
        const string json = """{ "type": "tool_use", "id": "toolu_1", "name": "Bash", "input": {"cmd":"ls"} }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var toolUse = Assert.IsType<ClaudeCodeToolUseBlock>(block);
        Assert.Equal("toolu_1", toolUse.Id);
        Assert.Equal("Bash", toolUse.Name);
    }

    [Fact]
    public void ContentBlock_Polymorphic_Deserializes_ToolResultBlock()
    {
        const string json = """{ "type": "tool_result", "tool_use_id": "toolu_1", "content": "ok", "is_error": false }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        var toolResult = Assert.IsType<ClaudeCodeToolResultBlock>(block);
        Assert.Equal("toolu_1", toolResult.ToolUseId);
        Assert.Equal("ok", toolResult.Content);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public void StreamMessage_UnknownType_DeserializesAsBaseType()
    {
        // A future message type we don't know about should fall back to base, not throw
        const string json = """{ "type": "heartbeat", "interval_ms": 1000 }""";

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        // Should not throw — falls back to base type
        Assert.IsType<ClaudeCodeStreamMessage>(result);
    }

    [Fact]
    public void ContentBlock_UnknownType_DeserializesAsBaseType()
    {
        const string json = """{ "type": "image", "source": {"type":"base64","media_type":"image/png","data":"abc"} }""";

        var block = JsonSerializer.Deserialize<ClaudeCodeContentBlock>(json, Options);

        Assert.IsType<ClaudeCodeContentBlock>(block);
    }

    [Fact]
    public void StreamMessage_MissingTypeDiscriminator_DeserializesAsBaseType()
    {
        const string json = """{ "some_field": "some_value" }""";

        var result = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(json, Options);

        Assert.IsType<ClaudeCodeStreamMessage>(result);
    }
}
