namespace WeaveFleet.Domain.Harnesses;

/// <summary>What a piece of running work is (<see cref="WorkReport.Kind"/>).</summary>
public static class WorkKinds
{
    /// <summary>Another agent working on part of the task, usually in a child session.</summary>
    public const string Subagent = "subagent";

    /// <summary>A shell command left running (a dev server, a test suite).</summary>
    public const string Shell = "shell";

    /// <summary>A watch on something (a log, a file) that reports what it sees.</summary>
    public const string Monitor = "monitor";

    /// <summary>Any other background job the harness runs.</summary>
    public const string Task = "task";

    public static bool IsKnown(string? kind) => kind is Subagent or Shell or Monitor or Task;
}

/// <summary>How a piece of running work ended (<see cref="WorkReport.EndedReason"/>).</summary>
public static class WorkEndedReasons
{
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    /// <summary>Fleet or the harness stopped while the work ran, and it went with them: nothing will report back.</summary>
    public const string Lost = "lost";

    public static bool IsKnown(string? reason) => reason is Completed or Error or Cancelled or Lost;
}

/// <summary>
/// What a harness says about one piece of running work, as the payload of <see cref="EventTypes.WorkStarted"/>,
/// <see cref="EventTypes.WorkUpdated"/> and <see cref="EventTypes.WorkEnded"/>. Fleet keeps one record per
/// <see cref="WorkId"/> in the session; an update changes only the fields it sets.
/// </summary>
public sealed record WorkReport
{
    /// <summary>The harness's own handle for the work: what it takes back in <see cref="IHarnessSession.StopWorkAsync"/>.</summary>
    public required string WorkId { get; init; }

    /// <summary>One of <see cref="WorkKinds"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>Its short name: the subagent's agent, or the tool that started it.</summary>
    public string? Title { get; init; }

    /// <summary>What it is doing: the subagent's task, the shell's command.</summary>
    public string? Label { get; init; }

    /// <summary>The tool call that started it, when there is one.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>The call that started it returned while the work carries on.</summary>
    public bool? Background { get; init; }

    /// <summary>
    /// The harness's id for the child session the work runs in, when it has one. Fleet makes it a hidden Fleet session
    /// of its own under the parent (as for a delegated subagent), and records that session's id.
    /// </summary>
    public string? ChildHarnessSessionId { get; init; }

    /// <summary>The harness can stop this item on its own (<see cref="IHarnessSession.StopWorkAsync"/>).</summary>
    public bool? CanStop { get; init; }

    /// <summary>The harness can page through this item's output (<see cref="IHarnessSession.ReadWorkOutputAsync"/>).</summary>
    public bool? CanReadOutput { get; init; }

    /// <summary>On <see cref="EventTypes.WorkEnded"/>: one of <see cref="WorkEndedReasons"/>.</summary>
    public string? EndedReason { get; init; }

    /// <summary>How it ended, in a few words (<c>exit 0</c>), or a short summary.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A page of a work item's output (<see cref="IHarnessSession.ReadWorkOutputAsync"/>): the text from the offset asked
/// for, the offset to ask for next, and how much there is so far. Offsets count bytes, as harnesses keep them.
/// </summary>
/// <param name="Truncated">The harness kept only part of the output; what's before it is gone.</param>
public sealed record WorkOutput(string Text, long NextOffset, long Size, bool Truncated);
