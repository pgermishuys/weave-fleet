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

        restored.Parts.Count.ShouldBe(1);
        var text = restored.Parts[0].ShouldBeOfType<TextPart>();
        text.Text.ShouldBe("Hello, world!");
        restored.Id.ShouldBe(original.Id);
        restored.Role.ShouldBe(original.Role);
        restored.Timestamp.ShouldBe(original.Timestamp);
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

        restored.Parts.Count.ShouldBe(1);
        var tool = restored.Parts[0].ShouldBeOfType<ToolUsePart>();
        tool.ToolCallId.ShouldBe("call-1");
        tool.ToolName.ShouldBe("my_tool");
        tool.State.ShouldBe(ToolUseState.Completed);
        tool.Arguments.GetProperty("foo").GetString().ShouldBe("bar");
        tool.Arguments.GetProperty("count").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void RoundTrip_ToolResultPart_PreservesContent()
    {
        var original = MakeMessage([
            new ToolResultPart("call-2", "result content", IsError: false)
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        restored.Parts.Count.ShouldBe(1);
        var result = restored.Parts[0].ShouldBeOfType<ToolResultPart>();
        result.ToolCallId.ShouldBe("call-2");
        result.Content.ShouldBe("result content");
        result.IsError.ShouldBeFalse();
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

        restored.Parts.Count.ShouldBe(4);
        restored.Parts[0].ShouldBeOfType<TextPart>();
        restored.Parts[1].ShouldBeOfType<ToolUsePart>();
        restored.Parts[2].ShouldBeOfType<ToolResultPart>();
        restored.Parts[3].ShouldBeOfType<TextPart>();
        restored.Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("Thinking...");
        restored.Parts[3].ShouldBeOfType<TextPart>().Text.ShouldBe("Done.");
    }

    [Fact]
    public void ToPersistedMessage_SetsSessionIdAndTimestamp()
    {
        var message = MakeMessage([new TextPart("hi")], role: "user");
        var sessionId = "my-session-99";

        var persisted = MessagePersistenceService.ToPersistedMessage(sessionId, message);

        persisted.SessionId.ShouldBe(sessionId);
        persisted.Id.ShouldBe(message.Id);
        persisted.Role.ShouldBe("user");
        persisted.Timestamp.ShouldBe(message.Timestamp.ToString("O"));
        string.IsNullOrEmpty(persisted.CreatedAt).ShouldBeFalse();
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
        restored.Parts.Count.ShouldBe(1);
        var text = restored.Parts[0].ShouldBeOfType<TextPart>();
        text.Text.ShouldBe("Hello world");
    }

    [Fact]
    public void MergePart_AddToolPart_ToEmptyParts()
    {
        var existing = MakePersistedMessage("[]");
        var args = JsonSerializer.SerializeToElement(new { input = "test" });
        var newPart = new ToolUsePart("call-99", "my_tool", args, ToolUseState.Pending);

        var result = MessagePersistenceService.MergePart(existing, newPart);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        restored.Parts.Count.ShouldBe(1);
        var tool = restored.Parts[0].ShouldBeOfType<ToolUsePart>();
        tool.ToolCallId.ShouldBe("call-99");
        tool.ToolName.ShouldBe("my_tool");
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
        restored.Parts.Count.ShouldBe(1); // replaced in-place, not appended
        var tool = restored.Parts[0].ShouldBeOfType<ToolUsePart>();
        tool.ToolCallId.ShouldBe("call-1");
        tool.State.ShouldBe(ToolUseState.Completed);
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
        restored.Parts.Count.ShouldBe(2);
        restored.Parts.All(static p => p is ToolUsePart).ShouldBeTrue();
    }

    [Fact]
    public void MergePart_TextPart_ReplacesFirstExistingTextPart()
    {
        var initial = MakeMessage([new TextPart("Old text")]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        var updatedText = new TextPart("New text");

        var result = MessagePersistenceService.MergePart(existingMsg, updatedText);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        restored.Parts.Count.ShouldBe(1); // replaced, not appended
        var text = restored.Parts[0].ShouldBeOfType<TextPart>();
        text.Text.ShouldBe("New text");
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
        restored.Parts.Count.ShouldBe(2);
        restored.Parts[0].ShouldBeOfType<ToolUsePart>();
        restored.Parts[1].ShouldBeOfType<TextPart>().Text.ShouldBe("Final answer");
    }
}
