namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when an app Fleet runs for a session starts, serves a page, restarts, stops or exits.
/// A browser canvas that shows the app updates its status from it.
/// </summary>
public sealed record AppUpdated : DomainEvent
{
    /// <summary>
    /// Gets the payload describing the app after the change.
    /// </summary>
    public required AppUpdatedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing an app after a change.
/// </summary>
public sealed record AppUpdatedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the app identifier, the same across restarts.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Gets the command Fleet runs.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Gets <c>starting</c>, <c>running</c>, <c>exited</c> or <c>stopped</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the page Fleet found, once one answers.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets the TCP ports the app's process tree listens on.
    /// </summary>
    public required IReadOnlyList<int> Ports { get; init; }

    /// <summary>
    /// Gets the exit code, once the process has exited on its own.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Gets what happened: <c>started</c>, <c>ready</c>, <c>restarted</c>, <c>stopped</c> or <c>exited</c>.
    /// </summary>
    public required string Reason { get; init; }
}
