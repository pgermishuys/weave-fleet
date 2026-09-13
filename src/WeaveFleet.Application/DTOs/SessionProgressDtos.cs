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
    string UpdatedAt);
