using System.Text.Json.Serialization;

namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when an agent turn has started.
/// </summary>
public sealed record TurnStarted : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the turn-started event.
    /// </summary>
    public required TurnStartedPayload Payload { get; init; }
}

/// <summary>
/// Raised when an agent turn has ended.
/// </summary>
public sealed record TurnEnded : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the turn-ended event.
    /// </summary>
    public required TurnEndedPayload Payload { get; init; }
}

/// <summary>
/// Raised when an agent turn stopped because the harness or the model provider failed, rather than
/// because the agent finished. Without it a failed turn is indistinguishable from a finished one:
/// the session simply goes idle with whatever text had already streamed.
/// </summary>
public sealed record TurnFailed : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the turn-failed event.
    /// </summary>
    public required TurnFailedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing a turn that failed.
/// </summary>
public sealed record TurnFailedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the Fleet message identifier of the assistant message the failure belongs to, when the
    /// turn had already produced one.
    /// </summary>
    [JsonPropertyName("messageID")]
    public string? MessageId { get; init; }

    /// <summary>
    /// Gets the failure the harness reported.
    /// </summary>
    public required TurnError Error { get; init; }
}

/// <summary>
/// A failure reported by a harness, normalised across harnesses so clients never parse
/// harness-specific error shapes.
/// </summary>
public sealed record TurnError
{
    /// <summary>
    /// Gets the harness's name for the failure (e.g. <c>APIError</c>), used to group failures.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the human-readable failure text to show the user.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets a value indicating whether the harness considered the failure worth retrying. A turn that
    /// fails this way has already exhausted the harness's own retries.
    /// </summary>
    public bool IsRetryable { get; init; }
}

/// <summary>
/// Payload describing a turn that has started.
/// </summary>
public sealed record TurnStartedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the Fleet message identifier associated with the turn.
    /// </summary>
    [JsonPropertyName("messageID")]
    public required string MessageId { get; init; }

    /// <summary>
    /// Gets the zero-based turn index within the message lifecycle.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the agent name that is handling the turn.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Gets the selected model identifier.
    /// </summary>
    [JsonPropertyName("modelID")]
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets the parent message identifier when the turn is part of a reply chain.
    /// </summary>
    [JsonPropertyName("parentID")]
    public string? ParentId { get; init; }
}

/// <summary>
/// Payload describing a turn that has ended.
/// </summary>
public sealed record TurnEndedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    [JsonPropertyName("sessionID")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the Fleet message identifier associated with the turn.
    /// </summary>
    [JsonPropertyName("messageID")]
    public required string MessageId { get; init; }

    /// <summary>
    /// Gets the zero-based turn index within the message lifecycle.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the completion reason reported for the turn.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the total cost reported for the completed turn.
    /// </summary>
    public double Cost { get; init; }

    /// <summary>
    /// Gets the token usage reported for the completed turn.
    /// </summary>
    public TurnTokenUsage? Tokens { get; init; }

    /// <summary>
    /// Gets the Unix timestamp in milliseconds when the turn completed.
    /// </summary>
    public long? CompletedAt { get; init; }
}

/// <summary>
/// Token usage reported for a completed turn.
/// </summary>
public sealed record TurnTokenUsage
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
