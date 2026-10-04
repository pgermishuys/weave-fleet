using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Entities;

/// <summary>
/// One piece of work an agent left running: a subagent in a child session (a delegation, which is where the table's
/// name comes from), a background shell, a monitor or another task.
/// </summary>
public sealed class Delegation
{
    public string Id { get; set; } = string.Empty;
    public string ParentSessionId { get; set; } = string.Empty;
    public string? ChildSessionId { get; set; }

    /// <summary>The tool call that started the work.</summary>
    public string? ParentToolCallId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary><c>pending</c> or <c>running</c> while it runs; <c>completed</c>, <c>error</c> or <c>cancelled</c> after.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When the work started.</summary>
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>When the work ended.</summary>
    public string? CompletedAt { get; set; }

    /// <summary>One of <see cref="WorkKinds"/>.</summary>
    public string Kind { get; set; } = WorkKinds.Subagent;

    /// <summary>The harness's own handle for the work; unique in its parent session.</summary>
    public string WorkId { get; set; } = string.Empty;

    /// <summary>What the work is: the subagent's task, the shell's command.</summary>
    public string? Label { get; set; }
    public bool Background { get; set; }
    public bool CanStop { get; set; }
    public bool CanReadOutput { get; set; }

    /// <summary>One of <see cref="WorkEndedReasons"/> once the work ended.</summary>
    public string? EndedReason { get; set; }
    public string? Detail { get; set; }

    /// <summary>Whether the work still runs.</summary>
    public bool IsRunning => Status is "pending" or "running";
}
