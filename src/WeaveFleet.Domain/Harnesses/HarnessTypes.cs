using System.Text.Json;
using System.Text.Json.Serialization;

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
    public bool SupportsDelegation { get; init; }
}

/// <summary>Whether a harness binary/service is available on this machine.</summary>
public sealed record HarnessAvailability(bool Available, string? Reason);

/// <summary>A real-time event emitted by a harness instance.</summary>
public sealed record HarnessEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public string? FleetSessionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public JsonElement? Payload { get; init; }
}

/// <summary>Discriminator for <see cref="MessagePart"/> subtypes.</summary>
public enum MessagePartKind { Text, ToolUse, ToolResult }

/// <summary>One logical piece of an agent message.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ToolUsePart), "tool")]
[JsonDerivedType(typeof(ToolResultPart), "tool-result")]
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

    /// <summary>The agent that produced this message (e.g. "loom", "thread").</summary>
    public string? Agent { get; init; }

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

/// <summary>Options for executing a slash command on an agent.</summary>
public sealed record CommandOptions
{
    /// <summary>Maximum allowed length for a command name.</summary>
    private const int MaxCommandLength = 64;

    /// <summary>Maximum allowed length for command arguments.</summary>
    private const int MaxArgumentsLength = 4096;

    public required string Command { get; init; }
    public string? Arguments { get; init; }
    public string? Agent { get; init; }
    public string? ModelId { get; init; }

    /// <summary>
    /// Validates the command name and arguments. Returns <c>null</c> when valid,
    /// or an error message describing the first violation found.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Command))
            return "Command name is required.";

        if (Command.Length > MaxCommandLength)
            return $"Command name exceeds {MaxCommandLength} characters.";

        foreach (var ch in Command)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('_' or '-'))
                return $"Command name contains invalid character '{ch}'. Only letters, digits, hyphens, and underscores are allowed.";
        }

        if (Arguments is not null && Arguments.Length > MaxArgumentsLength)
            return $"Arguments exceed {MaxArgumentsLength} characters.";

        return null;
    }
}

/// <summary>An agent available for selection in the UI.</summary>
public sealed record AgentInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Mode { get; init; }
    public bool Hidden { get; init; }
    public string? ModelProviderId { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>A slash command available for selection in the UI.</summary>
public sealed record CommandInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>A model provider with its available models.</summary>
public sealed record ProviderInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required IReadOnlyList<ModelInfo> Models { get; init; }
}

/// <summary>A model available within a provider.</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
}
