using System.Globalization;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Progress;

/// <summary>
/// Works out a session's progress from its events. Pure: callers load and store the result.
/// Knows nothing about harnesses; it only sees Fleet's own events.
/// </summary>
public static class SessionProgressTracker
{
    /// <summary>Event type pushed on the "sessions" topic with a <see cref="SessionProgressSummaryDto"/>.</summary>
    public const string SummaryEventType = "session_progress";

    /// <summary>Event type pushed on the session's own topic with a <see cref="SessionProgressDto"/>.</summary>
    public const string DetailEventType = "progress.updated";

    /// <summary>
    /// Applies a new todo list. Returns the new progress, or <see langword="null"/> when nothing changed
    /// (the same list again, or an empty list for a session that never had one).
    /// </summary>
    public static SessionProgress? ApplyTodos(
        SessionProgress? current,
        string sessionId,
        string userId,
        IReadOnlyList<TodoEntry> items,
        DateTimeOffset now)
    {
        if (current is null && items.Count == 0)
            return null;

        if (current is not null && current.Todos.SequenceEqual(items))
            return null;

        var counted = items.Where(item => item.Status != TodoStatuses.Cancelled).ToList();
        var currentItem = items.FirstOrDefault(item => item.Status == TodoStatuses.InProgress)
                          ?? items.FirstOrDefault(item => item.Status == TodoStatuses.Pending);

        return new SessionProgress
        {
            SessionId = sessionId,
            UserId = userId,
            Kind = SessionProgressKinds.Todos,
            Done = counted.Count(item => item.Status == TodoStatuses.Completed),
            Total = counted.Count,
            Current = currentItem?.Content,
            Todos = [.. items],
            UpdatedAt = now,
        };
    }

    public static SessionProgressSummaryDto ToSummary(SessionProgress progress)
        => new(progress.SessionId, progress.Kind, progress.Done, progress.Total, progress.Current);

    public static SessionProgressDto ToDto(SessionProgress progress)
        => new(
            progress.SessionId,
            progress.Kind,
            progress.Done,
            progress.Total,
            progress.Current,
            progress.Todos,
            progress.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
