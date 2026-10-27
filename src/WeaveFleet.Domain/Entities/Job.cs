namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A job in the queue: an ask for one agent session in a repository, started on whichever machine has a free seat
/// and fits it. Posted by the user; Fleet starts it by itself.
/// </summary>
public sealed record Job
{
    public required string Id { get; init; }

    public required string UserId { get; init; }

    public required string Title { get; init; }

    /// <summary>The prompt, sent to the session as it is.</summary>
    public required string Ask { get; init; }

    /// <summary>The repository's origin remote URL, or its folder name when it has no remote.</summary>
    public required string Repository { get; init; }

    /// <summary>Labels a machine must all have to take it.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>When set, only this machine may take it.</summary>
    public string? PinnedMachineId { get; init; }

    /// <summary>One of <see cref="JobStates"/>.</summary>
    public string State { get; init; } = JobStates.Waiting;

    /// <summary>The machine that took it; set from the moment its seat is claimed.</summary>
    public string? MachineId { get; init; }

    public string? MachineName { get; init; }

    /// <summary>Its session, on <see cref="MachineId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>Its session was seen in a turn. A session reads idle before its first turn starts, so idle alone isn't done.</summary>
    public bool Worked { get; init; }

    /// <summary>Why the last start failed, or why it failed.</summary>
    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public static class JobStates
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string NeedsYou = "needs_you";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>A job that holds a seat while its session works or waits on the user.</summary>
    public static bool HoldsSeat(string state) => state is Running or NeedsYou;
}

/// <summary>How many sessions Jobs may run at once on a machine, and what the machine is called for jobs that need it.</summary>
public sealed record JobMachineSettings(string MachineId, int Seats, IReadOnlyList<string> Labels);
