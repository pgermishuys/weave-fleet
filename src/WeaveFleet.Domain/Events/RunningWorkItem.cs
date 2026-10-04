namespace WeaveFleet.Domain.Events;

/// <summary>
/// One piece of work an agent left running, as Fleet shows it: in the session snapshot (<c>runningWork</c>), from
/// <c>GET /api/sessions/{id}/work</c> and <c>GET /api/work/running</c>, and as the payload of the <c>work.started</c>,
/// <c>work.updated</c> and <c>work.ended</c> events. The same shape for every harness; what a harness can't do shows as
/// <see cref="CanStop"/> or <see cref="CanReadOutput"/> false, or no <see cref="ChildSessionId"/>.
/// </summary>
public sealed record RunningWorkItem
{
    /// <summary>Fleet's id for the item; the REST actions (stop, output) take this.</summary>
    public required string Id { get; init; }

    /// <summary>The session whose agent started the work.</summary>
    public required string SessionId { get; init; }

    /// <summary>The harness's own handle for it (a shell id, a task id, a subagent's tool call).</summary>
    public required string WorkId { get; init; }

    /// <summary><c>subagent</c>, <c>shell</c>, <c>monitor</c> or <c>task</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Its short name: the subagent's agent, or the tool that started it.</summary>
    public required string Title { get; init; }

    /// <summary>What it is doing: the subagent's task, the shell's command.</summary>
    public string? Label { get; init; }

    /// <summary><c>pending</c> or <c>running</c> while it runs; <c>completed</c>, <c>error</c> or <c>cancelled</c> once ended.</summary>
    public required string Status { get; init; }

    /// <summary>The call that started it returned while the work carries on.</summary>
    public bool Background { get; init; }

    /// <summary>The Fleet session the work runs in, when it has one (a subagent's child session).</summary>
    public string? ChildSessionId { get; init; }

    /// <summary>The tool call that started it, when there is one; its card in the conversation.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Fleet can stop this item on its own: <c>POST /api/sessions/{id}/work/{workId}/stop</c>.</summary>
    public bool CanStop { get; init; }

    /// <summary>Fleet can read its output: <c>GET /api/sessions/{id}/work/{workId}/output</c>.</summary>
    public bool CanReadOutput { get; init; }

    /// <summary>When it started (ISO 8601).</summary>
    public required string StartedAt { get; init; }

    /// <summary>When it ended (ISO 8601); null while it runs.</summary>
    public string? EndedAt { get; init; }

    /// <summary><c>completed</c>, <c>error</c>, <c>cancelled</c> or <c>lost</c> once ended; null while it runs.</summary>
    public string? EndedReason { get; init; }

    /// <summary>How it ended, in a few words (<c>exit 0</c>).</summary>
    public string? Detail { get; init; }
}
