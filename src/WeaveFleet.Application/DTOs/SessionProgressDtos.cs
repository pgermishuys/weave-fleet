using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.DTOs;

/// <summary>
/// What a session row shows: sent in the session list and as <c>session_progress</c> on the "sessions" topic.
/// </summary>
public sealed record SessionProgressSummaryDto(
    string SessionId,
    string Kind,
    int Done,
    int Total,
    string? Current);

/// <summary>
/// Everything the open session shows: returned by <c>GET /api/sessions/{id}/progress</c> and sent as
/// <c>progress.updated</c> on the session's topic.
/// </summary>
public sealed record SessionProgressDto(
    string SessionId,
    string Kind,
    int Done,
    int Total,
    string? Current,
    IReadOnlyList<TodoEntry> Todos,
    string UpdatedAt)
{
    /// <summary>The plan the counts come from, when the session is working through one.</summary>
    public SessionPlanDto? Plan { get; init; }
}

/// <summary>A checklist plan as the Progress tab shows it.</summary>
/// <param name="Path">Where the file is, relative to the session's directory.</param>
/// <param name="TrackedSince">When Fleet first read the file; boxes ticked before then have no tick time.</param>
public sealed record SessionPlanDto(
    string Path,
    string? Title,
    string TrackedSince,
    IReadOnlyList<SessionPlanGroupDto> Groups);

public sealed record SessionPlanGroupDto(string? Title, IReadOnlyList<SessionPlanStepDto> Steps);

/// <param name="TickedAt">When Fleet saw the box get ticked; null if before it was watching, or not ticked.</param>
public sealed record SessionPlanStepDto(
    string Key,
    string? Number,
    string Title,
    bool Checked,
    int SubDone,
    int SubTotal,
    string? TickedAt,
    string? TickedInMessageId);
