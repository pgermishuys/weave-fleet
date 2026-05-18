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
    public void RoundTrip_ReasoningPart_ExcludesContentFromDurableHistory()
    {
        var original = MakeMessage([
            new ReasoningPart("Let me think", "summary")
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);
        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        restored.Parts.ShouldBeEmpty();
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

    [Fact]
    public void CreateUserPromptMessage_PreservesPromptAndAgent()
    {
        var timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero);

        var message = MessagePersistenceService.CreateUserPromptMessage("Hello from prompt", timestamp, "loom");

        message.Role.ShouldBe("user");
        message.Timestamp.ShouldBe(timestamp);
        message.Agent.ShouldBe("loom");
        message.Id.ShouldStartWith("user-");
        message.Parts.Count.ShouldBe(1);
        message.Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("Hello from prompt");
    }

    [Fact]
    public void CreateUserPromptMessage_WithUserMessageId_UsesSuppliedId()
    {
        var timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero);

        var message = MessagePersistenceService.CreateUserPromptMessage(
            "Hello from prompt",
            timestamp,
            "loom",
            "user-client-owned-id");

        message.Id.ShouldBe("user-client-owned-id");
        message.Role.ShouldBe("user");
        message.Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("Hello from prompt");
    }

    [Fact]
    public void CreateUserCommandMessage_PreservesSlashCommandAndSanitizesArguments()
    {
        var timestamp = new DateTimeOffset(2026, 4, 15, 7, 0, 0, TimeSpan.Zero);

        var message = MessagePersistenceService.CreateUserCommandMessage(
            new CommandOptions
            {
                Command = "review",
                Arguments = "line one\nline two",
                Agent = "loom"
            },
            timestamp);

        message.Role.ShouldBe("user");
        message.Timestamp.ShouldBe(timestamp);
        message.Agent.ShouldBe("loom");
        message.Parts.Count.ShouldBe(1);
        message.Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("/review line one line two");
    }

    [Fact]
    public void BuildCommittedMessagePayload_UsesPersistedSnapshotTextPartsOnly()
    {
        var persisted = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"Merged final text"},{"type":"reasoning","text":"Hidden thought"}]""",
            Timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero).ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = "loom",
        };

        var payload = MessagePersistenceService.BuildCommittedMessagePayload(persisted);

        payload.GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-1");
        payload.GetProperty("info").GetProperty("agent").GetString().ShouldBe("loom");
        var parts = payload.GetProperty("parts");
        parts.GetArrayLength().ShouldBe(1);
        parts[0].GetProperty("type").GetString().ShouldBe("text");
        parts[0].GetProperty("text").GetString().ShouldBe("Merged final text");
    }

    [Fact]
    public void SanitizeDurableEventPayload_DropsReasoningPartUpdates()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            part = new
            {
                id = "part-r1",
                messageID = "msg-1",
                sessionID = "session-1",
                type = "reasoning",
                text = "Hidden thought"
            }
        });

        var sanitized = MessagePersistenceService.SanitizeDurableEventPayload("message.part.updated", payload);

        sanitized.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void SanitizeDurableEventPayload_PreservesNonReasoningPartUpdates()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            part = new
            {
                id = "part-1",
                messageID = "msg-1",
                sessionID = "session-1",
                type = "text",
                text = "Visible text"
            }
        });

        var sanitized = MessagePersistenceService.SanitizeDurableEventPayload("message.part.updated", payload);

        sanitized.HasValue.ShouldBeTrue();
        sanitized.Value.GetProperty("part").GetProperty("type").GetString().ShouldBe("text");
    }

    [Fact]
    public void ToPersistedMessage_ExcludesReasoningPartsFromDurableHistory()
    {
        var original = MakeMessage([
            new TextPart("Visible text"),
            new ReasoningPart("Hidden thought")
        ]);

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", original);

        persisted.PartsJson.ShouldContain("Visible text");
        persisted.PartsJson.ShouldNotContain("Hidden thought");
    }

    [Fact]
    public void ToHarnessMessage_FiltersLegacyReasoningPartsFromDurableHistory()
    {
        var persisted = new PersistedMessage
        {
            Id = "msg-legacy-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"Visible text"},{"type":"reasoning","text":"Hidden thought"}]""",
            Timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero).ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var restored = MessagePersistenceService.ToHarnessMessage(persisted);

        restored.Parts.Count.ShouldBe(1);
        restored.Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("Visible text");
    }

    [Fact]
    public void MergePartAndMetadata_IgnoresReasoningPartsInDurableHistory()
    {
        var existing = MakePersistedMessage("""[{"type":"text","text":"Visible text"}]""");

        var result = MessagePersistenceService.MergePartAndMetadata(
            existing,
            new ReasoningPart("Hidden thought"),
            "assistant",
            null);

        result.PartsJson.ShouldContain("Visible text");
        result.PartsJson.ShouldNotContain("Hidden thought");
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

    [Fact]
    public void MergePart_ReasoningPart_DoesNotPersistHiddenContent()
    {
        var initial = MakeMessage([new ReasoningPart("Old thought", "old")]);
        var existingMsg = MessagePersistenceService.ToPersistedMessage("session-1", initial);

        var updatedReasoning = new ReasoningPart("New thought", "new");

        var result = MessagePersistenceService.MergePart(existingMsg, updatedReasoning);

        var restored = MessagePersistenceService.ToHarnessMessage(result);
        restored.Parts.ShouldBeEmpty();
    }

    [Fact]
    public void MergePartAndMetadata_BackfillsRoleAndAgent()
    {
        var existing = MakePersistedMessage("[]");
        existing.AgentName = null;

        var result = MessagePersistenceService.MergePartAndMetadata(
            existing,
            new TextPart("Hello world"),
            "user",
            "loom");

        result.Role.ShouldBe("user");
        result.AgentName.ShouldBe("loom");
        MessagePersistenceService.ToHarnessMessage(result)
            .Parts[0]
            .ShouldBeOfType<TextPart>()
            .Text.ShouldBe("Hello world");
    }

    [Fact]
    public void BuildCommittedMessagePayload_IncludesModelIdInInfo()
    {
        var persisted = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"Hello"}]""",
            Timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero).ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = "loom",
            ModelId = "claude-sonnet-4",
        };

        var payload = MessagePersistenceService.BuildCommittedMessagePayload(persisted);

        payload.GetProperty("info").GetProperty("modelID").GetString().ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public void BuildCommittedMessagePayload_OmitsModelIdWhenNull()
    {
        var persisted = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "user",
            PartsJson = """[{"type":"text","text":"Hello"}]""",
            Timestamp = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero).ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var payload = MessagePersistenceService.BuildCommittedMessagePayload(persisted);

        payload.GetProperty("info").TryGetProperty("modelID", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToPersistedMessage_CarriesModelId()
    {
        var message = new HarnessMessage
        {
            Id = "msg-1",
            Role = "assistant",
            Parts = [new TextPart("Hello")],
            Timestamp = DateTimeOffset.UtcNow,
            Agent = "loom",
            ModelId = "claude-sonnet-4",
        };

        var persisted = MessagePersistenceService.ToPersistedMessage("session-1", message);

        persisted.ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public void ToHarnessMessage_CarriesModelId()
    {
        var persisted = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"Hello"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = "loom",
            ModelId = "claude-sonnet-4",
        };

        var message = MessagePersistenceService.ToHarnessMessage(persisted);

        message.ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public void MergeTextDeltaAndMetadata_PreservesModelId()
    {
        var existing = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"Hello"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            ModelId = "claude-sonnet-4",
        };

        var result = MessagePersistenceService.MergeTextDeltaAndMetadata(existing, " world", "assistant", null);

        result.ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public void MergePartAndMetadata_PreservesModelId()
    {
        var existing = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            ModelId = "claude-sonnet-4",
        };

        var result = MessagePersistenceService.MergePartAndMetadata(existing, new TextPart("Hello"), "assistant", null);

        result.ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public void MergeMetadata_PreservesModelId()
    {
        var existing = new PersistedMessage
        {
            Id = "msg-1",
            SessionId = "session-1",
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            ModelId = "claude-sonnet-4",
        };

        var result = MessagePersistenceService.MergeMetadata(existing, "assistant", null);

        result.ModelId.ShouldBe("claude-sonnet-4");
    }
}

