using System.Collections.Immutable;
using NuCode.Sessions;

namespace NuCode;

public sealed class SessionModelTests
{
    [Fact]
    public void NuCodeSessionCreatesWithRequiredProperties()
    {
        var session = new NuCodeSession
        {
            Id = SessionId.New(),
            Slug = "test-session",
            Directory = "/workspace",
            Title = "Test Session",
            Version = "1.0.0",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        session.Slug.ShouldBe("test-session");
        session.Directory.ShouldBe("/workspace");
        session.ParentId.ShouldBeNull();
        session.ArchivedAt.ShouldBeNull();
        session.Summary.ShouldBeNull();
        session.Permissions.ShouldBeNull();
    }

    [Fact]
    public void NuCodeSessionSupportsParentChildRelationship()
    {
        var parentId = SessionId.New();
        var childSession = new NuCodeSession
        {
            Id = SessionId.New(),
            Slug = "child",
            Directory = "/workspace",
            Title = "Child",
            Version = "1.0.0",
            ParentId = parentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        childSession.ParentId.ShouldBe(parentId);
    }

    [Fact]
    public void SessionSummaryHoldsChangeCounts()
    {
        var summary = new SessionSummary(Additions: 10, Deletions: 3, Files: 2);

        summary.Additions.ShouldBe(10);
        summary.Deletions.ShouldBe(3);
        summary.Files.ShouldBe(2);
        summary.Diffs.ShouldBeNull();
    }

    [Fact]
    public void SessionRevertRecordsRevertPoint()
    {
        var msgId = MessageId.New();
        var partId = PartId.New();
        var revert = new SessionRevert(msgId, partId, Snapshot: "abc123");

        revert.MessageId.ShouldBe(msgId);
        revert.PartId.ShouldBe(partId);
        revert.Snapshot.ShouldBe("abc123");
    }

    [Fact]
    public void SessionStatusDiscriminatedTypes()
    {
        SessionStatus idle = new IdleSessionStatus();
        SessionStatus busy = new BusySessionStatus();
        SessionStatus retry = new RetrySessionStatus(2, "rate limited", DateTimeOffset.UtcNow.AddSeconds(30));

        idle.Type.ShouldBe("idle");
        busy.Type.ShouldBe("busy");
        retry.Type.ShouldBe("retry");

        var retryStatus = retry.ShouldBeOfType<RetrySessionStatus>();
        retryStatus.Attempt.ShouldBe(2);
        retryStatus.Message.ShouldBe("rate limited");
    }
}

public sealed class MessageModelTests
{
    [Fact]
    public void UserMessageHasCorrectRole()
    {
        var msg = new UserMessage(
            Id: MessageId.New(),
            SessionId: SessionId.New(),
            CreatedAt: DateTimeOffset.UtcNow,
            Agent: "build");

        msg.Role.ShouldBe(MessageRole.User);
        msg.Agent.ShouldBe("build");
        msg.ProviderId.ShouldBeNull();
        msg.ModelId.ShouldBeNull();
    }

    [Fact]
    public void AssistantMessageLinksToParent()
    {
        var parentId = MessageId.New();
        var msg = new AssistantMessage(
            Id: MessageId.New(),
            SessionId: SessionId.New(),
            CreatedAt: DateTimeOffset.UtcNow,
            ParentId: parentId,
            Agent: "build",
            ProviderId: "anthropic",
            ModelId: "claude-sonnet-4-20250514",
            Cost: 0.015m);

        msg.Role.ShouldBe(MessageRole.Assistant);
        msg.ParentId.ShouldBe(parentId);
        msg.Cost.ShouldBe(0.015m);
        msg.Error.ShouldBeNull();
        msg.IsSummary.ShouldBeFalse();
    }

    [Fact]
    public void MessageErrorDiscriminatedTypes()
    {
        MessageError authErr = new ProviderAuthError("anthropic", "Invalid API key");
        MessageError outputErr = new OutputLengthError();
        MessageError abortErr = new AbortedError("User cancelled");
        MessageError overflowErr = new ContextOverflowError("Context too large");
        MessageError apiErr = new ApiError("Rate limited", StatusCode: 429, IsRetryable: true);
        MessageError unknownErr = new UnknownMessageError("Something went wrong");

        authErr.Name.ShouldBe("ProviderAuthError");
        outputErr.Name.ShouldBe("MessageOutputLengthError");
        abortErr.Name.ShouldBe("MessageAbortedError");
        overflowErr.Name.ShouldBe("ContextOverflowError");
        apiErr.Name.ShouldBe("APIError");
        unknownErr.Name.ShouldBe("Unknown");

        var api = apiErr.ShouldBeOfType<ApiError>();
        api.StatusCode.ShouldBe(429);
        api.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public void MessageWithPartsAggregatesMessageAndParts()
    {
        var sessionId = SessionId.New();
        var msgId = MessageId.New();
        var msg = new UserMessage(msgId, sessionId, DateTimeOffset.UtcNow, "build");
        var parts = new MessagePart[]
        {
            new TextPart(PartId.New(), sessionId, msgId, "Hello"),
        };

        var withParts = new MessageWithParts(msg, parts);

        withParts.Message.ShouldBeSameAs(msg);
        withParts.Parts.ShouldHaveSingleItem();
    }
}

public sealed class MessagePartTests
{
    private readonly SessionId _sessionId = SessionId.New();
    private readonly MessageId _messageId = MessageId.New();

    [Fact]
    public void TextPartHasCorrectType()
    {
        var part = new TextPart(PartId.New(), _sessionId, _messageId, "Hello world");

        part.Type.ShouldBe("text");
        part.Text.ShouldBe("Hello world");
        part.Synthetic.ShouldBeFalse();
        part.Ignored.ShouldBeFalse();
    }

    [Fact]
    public void ReasoningPartTracksTime()
    {
        var start = DateTimeOffset.UtcNow;
        var part = new ReasoningPart(PartId.New(), _sessionId, _messageId, "Let me think...", start);

        part.Type.ShouldBe("reasoning");
        part.StartTime.ShouldBe(start);
        part.EndTime.ShouldBeNull();
    }

    [Fact]
    public void ToolPartStateTransitionsPendingToCompleted()
    {
        var input = ImmutableDictionary<string, object?>.Empty.Add("path", "/file.txt");
        var partId = PartId.New();

        // Pending
        var pending = new PendingToolCallState(input, """{"path":"/file.txt"}""");
        var toolPart = new ToolPart(partId, _sessionId, _messageId, "call_123", "read", pending);
        toolPart.State.Status.ShouldBe(ToolCallStatus.Pending);

        // Running
        var start = DateTimeOffset.UtcNow;
        var running = new RunningToolCallState(input, start, Title: "Reading file");
        toolPart = toolPart with { State = running };
        toolPart.State.Status.ShouldBe(ToolCallStatus.Running);

        // Completed
        var end = DateTimeOffset.UtcNow;
        var completed = new CompletedToolCallState(
            input, Output: "file contents", Title: "Read /file.txt",
            Metadata: ImmutableDictionary<string, object?>.Empty,
            StartTime: start, EndTime: end);
        toolPart = toolPart with { State = completed };
        toolPart.State.Status.ShouldBe(ToolCallStatus.Completed);
        ((CompletedToolCallState)toolPart.State).Output.ShouldBe("file contents");
    }

    [Fact]
    public void ToolPartStateTransitionsPendingToError()
    {
        var input = ImmutableDictionary<string, object?>.Empty;
        var start = DateTimeOffset.UtcNow;
        var error = new ErrorToolCallState(input, "Permission denied", start, DateTimeOffset.UtcNow);

        var toolPart = new ToolPart(PartId.New(), _sessionId, _messageId, "call_456", "bash", error);

        toolPart.State.Status.ShouldBe(ToolCallStatus.Error);
        ((ErrorToolCallState)toolPart.State).Error.ShouldBe("Permission denied");
    }

    [Fact]
    public void FilePartHasCorrectType()
    {
        var part = new FilePart(PartId.New(), _sessionId, _messageId, "image/png", "data:image/png;base64,...", "screenshot.png");

        part.Type.ShouldBe("file");
        part.Mime.ShouldBe("image/png");
        part.Filename.ShouldBe("screenshot.png");
    }

    [Fact]
    public void SnapshotPartHoldsState()
    {
        var part = new SnapshotPart(PartId.New(), _sessionId, _messageId, "abc123");

        part.Type.ShouldBe("snapshot");
        part.Snapshot.ShouldBe("abc123");
    }

    [Fact]
    public void PatchPartTracksFiles()
    {
        var files = ImmutableArray.Create("src/foo.cs", "src/bar.cs");
        var part = new PatchPart(PartId.New(), _sessionId, _messageId, "hash123", files);

        part.Type.ShouldBe("patch");
        part.Files.Length.ShouldBe(2);
    }

    [Fact]
    public void CompactionPartTracksAutoAndOverflow()
    {
        var part = new CompactionPart(PartId.New(), _sessionId, _messageId, Auto: true, Overflow: true);

        part.Type.ShouldBe("compaction");
        part.Auto.ShouldBeTrue();
        part.Overflow.ShouldBeTrue();
    }

    [Fact]
    public void SubtaskPartRecordsDelegation()
    {
        var part = new SubtaskPart(PartId.New(), _sessionId, _messageId,
            Prompt: "Explore the codebase", Description: "Find files", Agent: "explore");

        part.Type.ShouldBe("subtask");
        part.Agent.ShouldBe("explore");
    }

    [Fact]
    public void StepFinishPartTracksUsage()
    {
        var tokens = new TokenUsage(Input: 1000, Output: 500, Reasoning: 200, Cache: new CacheTokenUsage(100, 50), Total: 1700);
        var part = new StepFinishPart(PartId.New(), _sessionId, _messageId,
            Reason: "stop", Cost: 0.01m, Tokens: tokens);

        part.Type.ShouldBe("step-finish");
        part.Cost.ShouldBe(0.01m);
        part.Tokens.Total.ShouldBe(1700);
        part.Tokens.Cache.Read.ShouldBe(100);
    }

    [Fact]
    public void RetryPartTracksAttempt()
    {
        var part = new RetryPart(PartId.New(), _sessionId, _messageId,
            Attempt: 2, Error: "Rate limited", CreatedTime: DateTimeOffset.UtcNow);

        part.Type.ShouldBe("retry");
        part.Attempt.ShouldBe(2);
    }

    [Fact]
    public void AllPartTypesShareBaseProperties()
    {
        var parts = new MessagePart[]
        {
            new TextPart(PartId.New(), _sessionId, _messageId, "text"),
            new ReasoningPart(PartId.New(), _sessionId, _messageId, "think", DateTimeOffset.UtcNow),
            new ToolPart(PartId.New(), _sessionId, _messageId, "c1", "bash",
                new PendingToolCallState(ImmutableDictionary<string, object?>.Empty, "{}")),
            new FilePart(PartId.New(), _sessionId, _messageId, "text/plain", "data:..."),
            new SnapshotPart(PartId.New(), _sessionId, _messageId, "snap"),
            new PatchPart(PartId.New(), _sessionId, _messageId, "h", ImmutableArray<string>.Empty),
            new AgentPart(PartId.New(), _sessionId, _messageId, "build"),
            new CompactionPart(PartId.New(), _sessionId, _messageId, true),
            new SubtaskPart(PartId.New(), _sessionId, _messageId, "p", "d", "explore"),
            new RetryPart(PartId.New(), _sessionId, _messageId, 1, "err", DateTimeOffset.UtcNow),
            new StepStartPart(PartId.New(), _sessionId, _messageId),
            new StepFinishPart(PartId.New(), _sessionId, _messageId, "stop", 0m,
                new TokenUsage(0, 0, 0, new CacheTokenUsage(0, 0))),
        };

        // Every part should have a SessionId and MessageId
        foreach (var part in parts)
        {
            part.SessionId.ShouldBe(_sessionId);
            part.MessageId.ShouldBe(_messageId);
            string.IsNullOrEmpty(part.Type).ShouldBeFalse();
        }

        // Verify we have all 12 part types
        parts.Select(p => p.Type).Distinct().Count().ShouldBe(12);
    }
}
