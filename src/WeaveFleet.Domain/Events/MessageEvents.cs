using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Harnesses;

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
    /// Gets the message role: <c>user</c> (the prompt, which Fleet shows from its own send), <c>assistant</c> (a
    /// turn), or <c>notice</c> — something the harness put in the conversation itself, such as OpenCode 2's word
    /// that work it moved into the background has finished. A notice is nobody's turn: it isn't the session's last
    /// reply, and it carries no model.
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

    /// <summary>
    /// Gets the failure that ended this message, when the turn it belongs to failed. It rides on the
    /// message rather than only on <see cref="TurnFailed"/> so a session reloaded from a snapshot still
    /// shows why the turn stopped.
    /// </summary>
    /// <remarks>
    /// Serialised as <c>turnError</c>, not <c>error</c>: harnesses put their own differently shaped
    /// <c>error</c> on the message, and a harness shape would fail to bind to this one. Fleet fills this
    /// in from that raw shape instead, and the harness's own property is ignored.
    /// </remarks>
    [JsonPropertyName("turnError")]
    public TurnError? Error { get; init; }

    /// <summary>
    /// Gets the reason the model stopped producing this message (e.g. <c>stop</c>, <c>length</c>), when
    /// the harness reports one.
    /// </summary>
    public string? Finish { get; init; }

    /// <summary>
    /// Gets the slash command a user message came from, so the conversation shows <c>/name arguments</c> rather than
    /// the prompt the harness expanded it into.
    /// </summary>
    public SlashCommand? Command { get; init; }
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

    /// <summary>
    /// Gets whether the call itself has returned and only its work carries on, out of the turn: OpenCode 2 can move a
    /// shell command or a subagent into the background, and it then says so when the work finishes rather than by
    /// ending the call.
    /// </summary>
    public bool Background { get; init; }

    /// <summary>
    /// Gets what the call returned while its work goes on, when it returned anything: a call moved into the background
    /// answers at once with the handle its work carries on under.
    /// </summary>
    public JsonElement? Output { get; init; }

    /// <summary>
    /// Gets the optional tool metadata payload, which a harness can report while the call runs (a subagent's child
    /// session, a backgrounded call's shell).
    /// </summary>
    public JsonElement? Metadata { get; init; }
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

    /// <summary>
    /// Gets the harness's short heading for the call.
    /// </summary>
    public string? Title { get; init; }
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

    /// <summary>
    /// Gets the error text the tool failed with.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the optional tool metadata payload.
    /// </summary>
    public JsonElement? Metadata { get; init; }
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
