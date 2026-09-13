namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a terminal tab is added to a session's drawer. The client adds the tab from it.
/// </summary>
public sealed record TerminalOpened : DomainEvent
{
    /// <summary>
    /// Gets the payload naming the terminal.
    /// </summary>
    public required TerminalPayload Payload { get; init; }
}

/// <summary>
/// Raised when a terminal tab goes away: the user closed it, or its shell ended on its own.
/// </summary>
public sealed record TerminalClosed : DomainEvent
{
    /// <summary>
    /// Gets the payload naming the terminal.
    /// </summary>
    public required TerminalPayload Payload { get; init; }
}

/// <summary>
/// Payload naming one terminal in a session.
/// </summary>
public sealed record TerminalPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the terminal identifier.
    /// </summary>
    public required string TerminalId { get; init; }

    /// <summary>
    /// Gets the terminal's tab title, e.g. <c>zsh</c> or <c>zsh 2</c>.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the shell's exit code when it ended on its own; null otherwise.
    /// </summary>
    public int? ExitCode { get; init; }
}
