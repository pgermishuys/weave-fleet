using Microsoft.Extensions.Logging;

namespace NuCode.Tools;

/// <summary>
/// Context passed to tool execution, providing session state, configuration, and services.
/// </summary>
public sealed record ToolContext
{
    /// <summary>
    /// Gets the current session ID.
    /// </summary>
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Gets the current message ID.
    /// </summary>
    public required MessageId MessageId { get; init; }

    /// <summary>
    /// Gets the name of the agent invoking this tool.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Gets the working directory for file operations.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the cancellation token for cooperative cancellation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the logger for the tool.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Gets optional extra context data.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Extra { get; init; }
}
