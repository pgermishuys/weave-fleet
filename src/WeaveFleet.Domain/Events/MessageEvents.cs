using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a message has been created.
/// </summary>
public sealed record MessageCreated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the message-created event.
    /// </summary>
    public required MessageLifecyclePayload Payload { get; init; }
}

/// <summary>
/// Raised when a message has been updated.
/// </summary>
public sealed record MessageUpdated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the message-updated event.
    /// </summary>
    public required MessageLifecyclePayload Payload { get; init; }
}

/// <summary>
/// Raised when a single message part has been updated.
/// </summary>
public sealed record MessagePartUpdated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the message-part-updated event.
    /// </summary>
    public required MessagePartUpdatedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a streaming delta has been emitted for a message part.
/// </summary>
public sealed record MessagePartDeltaStreamed : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the message-part-delta-streamed event.
    /// </summary>
    public required MessagePartDeltaStreamedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing a created or updated message snapshot.
/// </summary>
public sealed record MessageLifecyclePayload
{
    /// <summary>
    /// Gets the current message metadata.
    /// </summary>
    public required MessageEventInfo Info { get; init; }

    /// <summary>
    /// Gets the current materialized set of message parts.
    /// </summary>
    public IReadOnlyList<MessageEventPart> Parts { get; init; } = [];
}

/// <summary>
/// Payload describing a single message-part update.
/// </summary>
public sealed record MessagePartUpdatedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier for the containing message.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the updated message part snapshot.
    /// </summary>
    public required MessageEventPart Part { get; init; }
}

/// <summary>
/// Payload describing a streamed delta for a message part.
/// </summary>
public sealed record MessagePartDeltaStreamedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier for the containing message.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the Fleet message identifier receiving the delta.
    /// </summary>
    [JsonPropertyName("messageID")]
    public required string MessageId { get; init; }

    /// <summary>
    /// Gets the identifier of the message part receiving the delta.
    /// </summary>
    [JsonPropertyName("partID")]
    public required string PartId { get; init; }

    /// <summary>
    /// Gets the field name being incrementally updated.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Gets the streamed text delta.
    /// </summary>
    public required string Delta { get; init; }
}

/// <summary>
/// Message metadata included with message lifecycle events.
/// </summary>
public sealed record MessageEventInfo
{
    /// <summary>
    /// Gets the Fleet message identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the message role.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the agent that produced the message.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Gets the model identifier that produced the message.
    /// </summary>
    [JsonPropertyName("modelID")]
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets the parent message identifier when the message is part of a reply chain.
    /// </summary>
    [JsonPropertyName("parentID")]
    public string? ParentId { get; init; }

    /// <summary>
    /// Gets the message timestamps.
    /// </summary>
    public required MessageEventTime Time { get; init; }

    /// <summary>
    /// Gets the total cost reported for the message.
    /// </summary>
    public double? Cost { get; init; }

    /// <summary>
    /// Gets the token usage reported for the message.
    /// </summary>
    public MessageTokenUsage? Tokens { get; init; }
}

/// <summary>
/// Timestamps included with a message lifecycle event.
/// </summary>
public sealed record MessageEventTime
{
    /// <summary>
    /// Gets the Unix timestamp in milliseconds when the message was created.
    /// </summary>
    public required long Created { get; init; }

    /// <summary>
    /// Gets the Unix timestamp in milliseconds when the message completed.
    /// </summary>
    public long? Completed { get; init; }
}

/// <summary>
/// Token usage reported for a message.
/// </summary>
public sealed record MessageTokenUsage
{
    /// <summary>
    /// Gets the number of input tokens consumed.
    /// </summary>
    public double Input { get; init; }

    /// <summary>
    /// Gets the number of output tokens produced.
    /// </summary>
    public double Output { get; init; }

    /// <summary>
    /// Gets the number of reasoning tokens consumed.
    /// </summary>
    public double Reasoning { get; init; }
}

/// <summary>
/// Base type for typed message-part snapshots carried by message events.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextMessageEventPart), "text")]
[JsonDerivedType(typeof(ReasoningMessageEventPart), "reasoning")]
[JsonDerivedType(typeof(ToolMessageEventPart), "tool")]
[JsonDerivedType(typeof(FileMessageEventPart), "file")]
[JsonDerivedType(typeof(StepStartedMessageEventPart), "step-start")]
[JsonDerivedType(typeof(StepFinishedMessageEventPart), "step-finish")]
public abstract record MessageEventPart
{
    /// <summary>
    /// Gets the message-part identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the Fleet session identifier for the containing message.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the Fleet message identifier for the containing message.
    /// </summary>
    [JsonPropertyName("messageID")]
    public required string MessageId { get; init; }
}

/// <summary>
/// A text message part snapshot.
/// </summary>
public sealed record TextMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the current text content.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>
/// A reasoning message part snapshot.
/// </summary>
public sealed record ReasoningMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the reasoning text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the optional summarized reasoning.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// A tool invocation message part snapshot.
/// </summary>
public sealed record ToolMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the tool name.
    /// </summary>
    [JsonPropertyName("tool")]
    public required string ToolName { get; init; }

    /// <summary>
    /// Gets the tool call identifier.
    /// </summary>
    [JsonPropertyName("callID")]
    public required string CallId { get; init; }

    /// <summary>
    /// Gets the current tool invocation state.
    /// </summary>
    public required ToolInvocationState State { get; init; }
}

/// <summary>
/// A file attachment message part snapshot.
/// </summary>
public sealed record FileMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the attachment MIME type.
    /// </summary>
    public required string Mime { get; init; }

    /// <summary>
    /// Gets the attachment URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the optional attachment filename.
    /// </summary>
    public string? Filename { get; init; }
}

/// <summary>
/// A step-start message part snapshot.
/// </summary>
public sealed record StepStartedMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the zero-based step index.
    /// </summary>
    public required int Index { get; init; }
}

/// <summary>
/// A step-finish message part snapshot.
/// </summary>
public sealed record StepFinishedMessageEventPart : MessageEventPart
{
    /// <summary>
    /// Gets the zero-based step index.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the completion reason.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the cost reported for the completed step.
    /// </summary>
    public double Cost { get; init; }

    /// <summary>
    /// Gets the token usage reported for the completed step.
    /// </summary>
    public MessageTokenUsage? Tokens { get; init; }

    /// <summary>
    /// Gets the Unix timestamp in milliseconds when the step completed.
    /// </summary>
    public long? CompletedAt { get; init; }
}

/// <summary>
/// Base type for typed tool invocation states.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(ToolPendingState), "pending")]
[JsonDerivedType(typeof(ToolRunningState), "running")]
[JsonDerivedType(typeof(ToolCompletedState), "completed")]
[JsonDerivedType(typeof(ToolErrorState), "error")]
[JsonDerivedType(typeof(ToolCancelledState), "cancelled")]
public abstract record ToolInvocationState;

/// <summary>
/// A pending tool invocation state.
/// </summary>
public sealed record ToolPendingState : ToolInvocationState
{
    /// <summary>
    /// Gets the typed input payload for the tool invocation.
    /// </summary>
    public JsonElement? Input { get; init; }
}

/// <summary>
/// A running tool invocation state.
/// </summary>
public sealed record ToolRunningState : ToolInvocationState
{
    /// <summary>
    /// Gets the typed input payload for the tool invocation.
    /// </summary>
    public JsonElement? Input { get; init; }
}

/// <summary>
/// A completed tool invocation state.
/// </summary>
public sealed record ToolCompletedState : ToolInvocationState
{
    /// <summary>
    /// Gets the typed input payload for the tool invocation.
    /// </summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    /// Gets the typed output payload for the tool invocation.
    /// </summary>
    public JsonElement? Output { get; init; }

    /// <summary>
    /// Gets the optional tool metadata payload.
    /// </summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// A failed tool invocation state.
/// </summary>
public sealed record ToolErrorState : ToolInvocationState
{
    /// <summary>
    /// Gets the typed input payload for the tool invocation.
    /// </summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    /// Gets the typed output payload for the tool invocation.
    /// </summary>
    public JsonElement? Output { get; init; }
}

/// <summary>
/// A cancelled tool invocation state.
/// </summary>
public sealed record ToolCancelledState : ToolInvocationState
{
    /// <summary>
    /// Gets the typed input payload for the tool invocation.
    /// </summary>
    public JsonElement? Input { get; init; }
}
