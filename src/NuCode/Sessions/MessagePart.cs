using System.Collections.Immutable;

namespace NuCode.Sessions;

/// <summary>
/// Base type for all message parts. Parts are discriminated by <see cref="Type"/>.
/// Each part belongs to a specific message within a session.
/// </summary>
public abstract record MessagePart(
    string Type,
    PartId Id,
    SessionId SessionId,
    MessageId MessageId);

/// <summary>
/// A text content part (user input or assistant response text).
/// </summary>
public sealed record TextPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Text,
    bool Synthetic = false,
    bool Ignored = false,
    DateTimeOffset? StartTime = null,
    DateTimeOffset? EndTime = null)
    : MessagePart("text", Id, SessionId, MessageId);

/// <summary>
/// A chain-of-thought reasoning part from the model.
/// </summary>
public sealed record ReasoningPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Text,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime = null)
    : MessagePart("reasoning", Id, SessionId, MessageId);

/// <summary>
/// A tool invocation part with state machine tracking.
/// </summary>
public sealed record ToolPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string CallId,
    string ToolName,
    ToolCallState State)
    : MessagePart("tool", Id, SessionId, MessageId);

/// <summary>
/// State of a tool call, discriminated by <see cref="Status"/>.
/// </summary>
public abstract record ToolCallState(ToolCallStatus Status, ImmutableDictionary<string, object?> Input);

/// <summary>Tool call is queued, not yet started.</summary>
public sealed record PendingToolCallState(
    ImmutableDictionary<string, object?> Input,
    string RawInput)
    : ToolCallState(ToolCallStatus.Pending, Input);

/// <summary>Tool call is executing.</summary>
public sealed record RunningToolCallState(
    ImmutableDictionary<string, object?> Input,
    DateTimeOffset StartTime,
    string? Title = null)
    : ToolCallState(ToolCallStatus.Running, Input);

/// <summary>Tool call completed successfully.</summary>
public sealed record CompletedToolCallState(
    ImmutableDictionary<string, object?> Input,
    string Output,
    string Title,
    ImmutableDictionary<string, object?> Metadata,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    DateTimeOffset? CompactedTime = null,
    ImmutableArray<FilePart>? Attachments = null)
    : ToolCallState(ToolCallStatus.Completed, Input);

/// <summary>Tool call failed with an error.</summary>
public sealed record ErrorToolCallState(
    ImmutableDictionary<string, object?> Input,
    string Error,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime)
    : ToolCallState(ToolCallStatus.Error, Input);

/// <summary>
/// A file attachment part (image, document, etc.).
/// </summary>
public sealed record FilePart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Mime,
    string Url,
    string? Filename = null)
    : MessagePart("file", Id, SessionId, MessageId);

/// <summary>
/// A repository state snapshot part.
/// </summary>
public sealed record SnapshotPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Snapshot)
    : MessagePart("snapshot", Id, SessionId, MessageId);

/// <summary>
/// A code change patch part.
/// </summary>
public sealed record PatchPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Hash,
    ImmutableArray<string> Files)
    : MessagePart("patch", Id, SessionId, MessageId);

/// <summary>
/// Marks the agent that produced this portion of the conversation.
/// </summary>
public sealed record AgentPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Name)
    : MessagePart("agent", Id, SessionId, MessageId);

/// <summary>
/// Marks a context compaction boundary.
/// </summary>
public sealed record CompactionPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    bool Auto,
    bool Overflow = false)
    : MessagePart("compaction", Id, SessionId, MessageId);

/// <summary>
/// A subtask delegation part (agent delegating to subagent).
/// </summary>
public sealed record SubtaskPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Prompt,
    string Description,
    string Agent,
    string? Command = null)
    : MessagePart("subtask", Id, SessionId, MessageId);

/// <summary>
/// Records an error retry attempt.
/// </summary>
public sealed record RetryPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    int Attempt,
    string Error,
    DateTimeOffset CreatedTime)
    : MessagePart("retry", Id, SessionId, MessageId);

/// <summary>
/// Marks the start of an agent processing step.
/// </summary>
public sealed record StepStartPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string? Snapshot = null)
    : MessagePart("step-start", Id, SessionId, MessageId);

/// <summary>
/// Marks the end of an agent processing step with usage info.
/// </summary>
public sealed record StepFinishPart(
    PartId Id,
    SessionId SessionId,
    MessageId MessageId,
    string Reason,
    decimal Cost,
    TokenUsage Tokens,
    string? Snapshot = null)
    : MessagePart("step-finish", Id, SessionId, MessageId);

/// <summary>
/// Token usage breakdown for a processing step.
/// </summary>
public sealed record TokenUsage(
    int Input,
    int Output,
    int Reasoning,
    CacheTokenUsage Cache,
    int? Total = null);

/// <summary>
/// Cache token usage breakdown.
/// </summary>
public sealed record CacheTokenUsage(int Read, int Write);
