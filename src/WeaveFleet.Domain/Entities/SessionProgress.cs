using System.Text.Json.Serialization;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Domain.Entities;

/// <summary>
/// How far along a session is, as Fleet last worked it out from the session's events.
/// </summary>
public sealed record SessionProgress
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }

    /// <summary>What the counts are counting, one of <see cref="SessionProgressKinds"/>.</summary>
    public required string Kind { get; init; }

    public int Done { get; init; }

    /// <summary>Items that count towards progress. Cancelled todos don't.</summary>
    public int Total { get; init; }

    /// <summary>The item being worked on, or the next one when nothing is in progress.</summary>
    public string? Current { get; init; }

    /// <summary>The agent's todo list as it last wrote it.</summary>
    public IReadOnlyList<TodoEntry> Todos { get; init; } = [];

    /// <summary>Checklist files the session wrote, most recently written first. The counts come from the active one.</summary>
    public IReadOnlyList<TrackedPlan> Plans { get; init; } = [];

    /// <summary>Subagents the session started, oldest first, each with its own progress.</summary>
    public IReadOnlyList<TrackedSubagent> Subagents { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>What a session's progress counts.</summary>
public static class SessionProgressKinds
{
    /// <summary>The agent's todo list.</summary>
    public const string Todos = "todos";

    /// <summary>A checklist plan the session is working through.</summary>
    public const string Plan = "plan";
}

/// <summary>A subagent the session started, and how far along its own session is.</summary>
public sealed record TrackedSubagent
{
    public required string DelegationId { get; init; }

    /// <summary>The subagent's own Fleet session, once it's known.</summary>
    public string? ChildSessionId { get; init; }

    /// <summary>The kind of subagent, such as <c>shuttle</c>.</summary>
    public required string Agent { get; init; }

    /// <summary>The subagent session's title, usually the task it was given.</summary>
    public string? Title { get; init; }

    /// <summary><c>pending</c>, <c>running</c>, <c>completed</c>, <c>error</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The plan step that was current when the subagent started; null without a plan.</summary>
    public string? StepKey { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public string? Current { get; init; }
}

/// <summary>A markdown checklist the session wrote, as Fleet last read it.</summary>
public sealed record TrackedPlan
{
    /// <summary>Where the file is, relative to the session's directory, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<TrackedPlanGroup> Groups { get; init; } = [];

    /// <summary>When Fleet first read the file. Boxes already ticked then have no tick time.</summary>
    public DateTimeOffset TrackedSince { get; init; }

    public DateTimeOffset LastWrittenAt { get; init; }

    public DateTimeOffset? LastTickedAt { get; init; }

    [JsonIgnore]
    public IEnumerable<TrackedPlanStep> Steps => Groups.SelectMany(group => group.Steps);
}

/// <summary>Steps under one heading of a plan.</summary>
public sealed record TrackedPlanGroup
{
    public string? Title { get; init; }
    public IReadOnlyList<TrackedPlanStep> Steps { get; init; } = [];
}

/// <summary>One checkbox step of a plan.</summary>
public sealed record TrackedPlanStep
{
    /// <summary>What identifies the step between versions of the file: its number, else its title.</summary>
    public required string Key { get; init; }

    public string? Number { get; init; }
    public required string Title { get; init; }
    public bool Checked { get; init; }
    public int SubDone { get; init; }
    public int SubTotal { get; init; }

    /// <summary>When Fleet saw the box get ticked; null if it was ticked before Fleet was watching.</summary>
    public DateTimeOffset? TickedAt { get; init; }

    /// <summary>The message whose tool call ticked the box, when known.</summary>
    public string? TickedInMessageId { get; init; }
}
