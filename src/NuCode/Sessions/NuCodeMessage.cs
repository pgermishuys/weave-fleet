namespace NuCode.Sessions;

/// <summary>
/// The role of a message in a conversation.
/// </summary>
public enum MessageRole
{
    /// <summary>A message from the user.</summary>
    User,

    /// <summary>A message from the assistant (model).</summary>
    Assistant,
}

/// <summary>
/// Base type for messages. Discriminated by <see cref="Role"/>.
/// </summary>
public abstract record NuCodeMessage(
    MessageId Id,
    SessionId SessionId,
    MessageRole Role,
    DateTimeOffset CreatedAt);

/// <summary>
/// A user-originated message.
/// </summary>
public sealed record UserMessage(
    MessageId Id,
    SessionId SessionId,
    DateTimeOffset CreatedAt,
    string Agent,
    string? ProviderId = null,
    string? ModelId = null,
    string? SystemPrompt = null)
    : NuCodeMessage(Id, SessionId, MessageRole.User, CreatedAt);

/// <summary>
/// An assistant-generated message.
/// </summary>
public sealed record AssistantMessage(
    MessageId Id,
    SessionId SessionId,
    DateTimeOffset CreatedAt,
    MessageId ParentId,
    string Agent,
    string ProviderId,
    string ModelId,
    decimal Cost = 0m,
    TokenUsage? Tokens = null,
    DateTimeOffset? CompletedAt = null,
    string? FinishReason = null,
    bool IsSummary = false,
    MessageError? Error = null)
    : NuCodeMessage(Id, SessionId, MessageRole.Assistant, CreatedAt);

/// <summary>
/// Base type for message errors. Discriminated by <see cref="Name"/>.
/// </summary>
public abstract record MessageError(string Name, string Message);

/// <summary>Provider authentication error.</summary>
public sealed record ProviderAuthError(string ProviderId, string Message)
    : MessageError("ProviderAuthError", Message);

/// <summary>Model output was truncated due to length limits.</summary>
public sealed record OutputLengthError()
    : MessageError("MessageOutputLengthError", "Output length exceeded");

/// <summary>The operation was aborted by the user.</summary>
public sealed record AbortedError(string Message)
    : MessageError("MessageAbortedError", Message);

/// <summary>Context window overflow.</summary>
public sealed record ContextOverflowError(string Message, string? ResponseBody = null)
    : MessageError("ContextOverflowError", Message);

/// <summary>API-level error from the LLM provider.</summary>
public sealed record ApiError(
    string Message,
    int? StatusCode = null,
    bool IsRetryable = false,
    string? ResponseBody = null)
    : MessageError("APIError", Message);

/// <summary>Unknown/unclassified error.</summary>
public sealed record UnknownMessageError(string Message)
    : MessageError("Unknown", Message);

/// <summary>
/// A message together with its parts.
/// </summary>
public sealed record MessageWithParts(NuCodeMessage Message, IReadOnlyList<MessagePart> Parts);
