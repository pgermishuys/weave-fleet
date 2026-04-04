using System.Text.Json;

namespace WeaveFleet.Domain.Harnesses;

/// <summary>Lifecycle status of a running harness instance.</summary>
public enum HarnessInstanceStatus
{
    Starting,
    Running,
    Idle,
    Stopping,
    Stopped,
    Error
}

/// <summary>Declares what a harness supports so the frontend can adapt its UI.</summary>
public sealed record HarnessCapabilities
{
    public bool RequiresInitialPrompt { get; init; }
    public bool SupportsAgents { get; init; }
    public bool SupportsModelSelection { get; init; }
    public bool SupportsCommands { get; init; }
    public bool SupportsForking { get; init; }
    public bool SupportsResume { get; init; }
    public bool SupportsImageAttachments { get; init; }
    public bool SupportsStreaming { get; init; }
}

/// <summary>Whether a harness binary/service is available on this machine.</summary>
public sealed record HarnessAvailability(bool Available, string? Reason);

/// <summary>A real-time event emitted by a harness instance.</summary>
public sealed record HarnessEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public JsonElement? Payload { get; init; }
}

/// <summary>Discriminator for <see cref="MessagePart"/> subtypes.</summary>
public enum MessagePartKind { Text, ToolUse, ToolResult }

/// <summary>One logical piece of an agent message.</summary>
public abstract record MessagePart(MessagePartKind Kind);

/// <summary>Plain text content.</summary>
public sealed record TextPart(string Text) : MessagePart(MessagePartKind.Text);

/// <summary>The agent invoking a tool.</summary>
public sealed record ToolUsePart(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    ToolUseState State) : MessagePart(MessagePartKind.ToolUse);

/// <summary>Output returned by a tool invocation.</summary>
public sealed record ToolResultPart(
    string ToolCallId,
    string Content,
    bool IsError) : MessagePart(MessagePartKind.ToolResult);

/// <summary>Lifecycle state of a tool invocation.</summary>
public enum ToolUseState { Pending, Running, Completed, Error }

/// <summary>Normalized message from an agent conversation.</summary>
public sealed record HarnessMessage
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<MessagePart> Parts { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Convenience: concatenated text parts.</summary>
    public string TextContent =>
        string.Join("", Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>Query parameters for paginated message retrieval.</summary>
public sealed record MessageQuery(int? Limit = null, string? Before = null);

/// <summary>A page of messages with a continuation flag.</summary>
public sealed record MessagePage(IReadOnlyList<HarnessMessage> Messages, bool HasMore);

/// <summary>Result of a health check on a harness instance.</summary>
public sealed record HealthCheckResult(bool Healthy, string? Message);

/// <summary>A file or image attached to a prompt.</summary>
public sealed record HarnessAttachment(string Mime, string Filename, string Data);

/// <summary>Options for sending a prompt to an agent.</summary>
public sealed record PromptOptions
{
    public string? Agent { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<HarnessAttachment>? Attachments { get; init; }
}
