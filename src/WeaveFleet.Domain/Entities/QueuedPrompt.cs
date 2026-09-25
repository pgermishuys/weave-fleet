namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A message the user queued while the session's agent was working. Fleet sends it when the turn ends, in the order
/// queued; it's kept in Fleet's database, so leaving the session or closing the browser doesn't lose it.
/// </summary>
public sealed record QueuedPrompt
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }

    /// <summary>How it goes out: one of <see cref="QueuedPromptKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>What the user typed, as the queue shows it.</summary>
    public required string Text { get; init; }

    /// <summary>For a <see cref="QueuedPromptKinds.Command"/>: the command's name and its arguments.</summary>
    public string? Command { get; init; }
    public string? Arguments { get; init; }

    /// <summary>What the composer had picked when it was queued; left out, the session's own.</summary>
    public string? Agent { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Effort { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>How a <see cref="QueuedPrompt"/> goes out.</summary>
public static class QueuedPromptKinds
{
    /// <summary>A message to the agent: a turn of its own.</summary>
    public const string Prompt = "prompt";

    /// <summary>A slash command, run as a command: a turn of its own.</summary>
    public const string Command = "command";

    /// <summary>A shell command the user runs in the session's folder (<c>!git status</c>): no turn.</summary>
    public const string Shell = "shell";

    public static bool IsKnown(string? kind) => kind is Prompt or Command or Shell;
}
