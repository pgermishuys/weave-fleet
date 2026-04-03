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

/// <summary>
/// Normalized message from an agent conversation.
/// Fields are intentionally minimal — concrete shape will be refined
/// when the first harness implementation is built.
/// </summary>
public sealed record HarnessMessage
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

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
