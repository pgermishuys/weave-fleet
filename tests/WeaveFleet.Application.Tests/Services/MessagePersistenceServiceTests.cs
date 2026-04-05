using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Services;

public sealed class MessagePersistenceServiceTests
{
    private static HarnessMessage MakeMessage(IReadOnlyList<MessagePart> parts, string? role = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Role = role ?? "assistant",
            Parts = parts,
            Timestamp = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public void RoundTrip_TextPart_PreservesContent()
    {
        var original = MakeMessage([new TextPart("Hello, world!")]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        Assert.Single(restored.Parts);
        var text = Assert.IsType<TextPart>(restored.Parts[0]);
        Assert.Equal("Hello, world!", text.Text);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Role, restored.Role);
        Assert.Equal(original.Timestamp, restored.Timestamp);
    }

    [Fact]
    public void RoundTrip_ToolUsePart_PreservesAllFields()
    {
        var args = JsonSerializer.SerializeToElement(new { foo = "bar", count = 42 });
        var original = MakeMessage([
            new ToolUsePart("call-1", "my_tool", args, ToolUseState.Completed)
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        Assert.Single(restored.Parts);
        var tool = Assert.IsType<ToolUsePart>(restored.Parts[0]);
        Assert.Equal("call-1", tool.ToolCallId);
        Assert.Equal("my_tool", tool.ToolName);
        Assert.Equal(ToolUseState.Completed, tool.State);
        Assert.Equal("bar", tool.Arguments.GetProperty("foo").GetString());
        Assert.Equal(42, tool.Arguments.GetProperty("count").GetInt32());
    }

    [Fact]
    public void RoundTrip_ToolResultPart_PreservesContent()
    {
        var original = MakeMessage([
            new ToolResultPart("call-2", "result content", IsError: false)
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        Assert.Single(restored.Parts);
        var result = Assert.IsType<ToolResultPart>(restored.Parts[0]);
        Assert.Equal("call-2", result.ToolCallId);
        Assert.Equal("result content", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public void RoundTrip_MixedParts_PreservesOrder()
    {
        var args = JsonSerializer.SerializeToElement(new { x = 1 });
        var original = MakeMessage([
            new TextPart("Thinking..."),
            new ToolUsePart("call-3", "search", args, ToolUseState.Running),
            new ToolResultPart("call-3", "found it", IsError: false),
            new TextPart("Done."),
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        Assert.Equal(4, restored.Parts.Count);
        Assert.IsType<TextPart>(restored.Parts[0]);
        Assert.IsType<ToolUsePart>(restored.Parts[1]);
        Assert.IsType<ToolResultPart>(restored.Parts[2]);
        Assert.IsType<TextPart>(restored.Parts[3]);
        Assert.Equal("Thinking...", ((TextPart)restored.Parts[0]).Text);
        Assert.Equal("Done.", ((TextPart)restored.Parts[3]).Text);
    }

    [Fact]
    public void ToPersistedMessage_SetsSessionIdAndTimestamp()
    {
        var message = MakeMessage([new TextPart("hi")], role: "user");
        var sessionId = "my-session-99";

        var persisted = MessagePersistenceService.ToPersistedMessage(sessionId, message);

        Assert.Equal(sessionId, persisted.SessionId);
        Assert.Equal(message.Id, persisted.Id);
        Assert.Equal("user", persisted.Role);
        Assert.Equal(message.Timestamp.ToString("O"), persisted.Timestamp);
        Assert.False(string.IsNullOrEmpty(persisted.CreatedAt));
    }

    // -----------------------------------------------------------------------
    // MergePart tests
    // -----------------------------------------------------------------------

    private static PersistedMessage MakePersistedMessage(
        string partsJson = "[]",
        string? messageId = null,
        string? sessionId = null) =>
        new()
        {
            Id = messageId ?? Guid.NewGuid().ToString(),
            SessionId = sessionId ?? "session-1",
            Role = "assistant",
            PartsJson = partsJson,
            Timestamp = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero).ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

    [Fact]
    public void MergePart_AddTextPart_ToEmptyParts()
    {
        var existing = MakePersistedMessage("[]");
        var newPart = new TextPart("Hello world");

        var result = MessagePersistenceService.MergePart(existing, newPart);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Single(restored.Parts);
        var text = Assert.IsType<TextPart>(restored.Parts[0]);
        Assert.Equal("Hello world", text.Text);
    }

    [Fact]
    public void MergePart_AddToolPart_ToEmptyParts()
    {
        var existing = MakePersistedMessage("[]");
        var args = JsonSerializer.SerializeToElement(new { input = "test" });
        var newPart = new ToolUsePart("call-99", "my_tool", args, ToolUseState.Pending);

        var result = MessagePersistenceService.MergePart(existing, newPart);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Single(restored.Parts);
        var tool = Assert.IsType<ToolUsePart>(restored.Parts[0]);
        Assert.Equal("call-99", tool.ToolCallId);
        Assert.Equal("my_tool", tool.ToolName);
    }

    [Fact]
    public void MergePart_UpdateExistingToolPart_MatchedByToolCallId()
    {
        // Existing message has a pending tool call
        var emptyArgs = JsonSerializer.SerializeToElement(new { });
        var initial = MakeMessage([
            new ToolUsePart("call-1", "search", emptyArgs, ToolUseState.Pending)
        ]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        // Merge a completed version of the same tool call
        var args = JsonSerializer.SerializeToElement(new { query = "hello" });
        var updatedPart = new ToolUsePart("call-1", "search", args, ToolUseState.Completed);

        var result = MessagePersistenceService.MergePart(existingMsg, updatedPart);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Single(restored.Parts); // replaced in-place, not appended
        var tool = Assert.IsType<ToolUsePart>(restored.Parts[0]);
        Assert.Equal("call-1", tool.ToolCallId);
        Assert.Equal(ToolUseState.Completed, tool.State);
    }

    [Fact]
    public void MergePart_UnknownToolCallId_AppendsNewToolPart()
    {
        var emptyArgs = JsonSerializer.SerializeToElement(new { });
        var initial = MakeMessage([
            new ToolUsePart("call-1", "search", emptyArgs, ToolUseState.Completed)
        ]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        var newPart = new ToolUsePart("call-2", "write", emptyArgs, ToolUseState.Pending);

        var result = MessagePersistenceService.MergePart(existingMsg, newPart);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Equal(2, restored.Parts.Count);
        Assert.All(restored.Parts, p => Assert.IsType<ToolUsePart>(p));
    }

    [Fact]
    public void MergePart_TextPart_ReplacesFirstExistingTextPart()
    {
        var initial = MakeMessage([new TextPart("Old text")]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        var updatedText = new TextPart("New text");

        var result = MessagePersistenceService.MergePart(existingMsg, updatedText);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Single(restored.Parts); // replaced, not appended
        var text = Assert.IsType<TextPart>(restored.Parts[0]);
        Assert.Equal("New text", text.Text);
    }

    [Fact]
    public void MergePart_TextPart_DoesNotReplaceToolParts()
    {
        // A message with only a tool part; adding a text part should append
        var emptyArgs = JsonSerializer.SerializeToElement(new { });
        var initial = MakeMessage([
            new ToolUsePart("call-1", "search", emptyArgs, ToolUseState.Completed)
        ]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        var newText = new TextPart("Final answer");

        var result = MessagePersistenceService.MergePart(existingMsg, newText);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        Assert.Equal(2, restored.Parts.Count);
        Assert.IsType<ToolUsePart>(restored.Parts[0]);
        Assert.IsType<TextPart>(restored.Parts[1]);
        Assert.Equal("Final answer", ((TextPart)restored.Parts[1]).Text);
    }
}
