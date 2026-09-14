using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Progress;

/// <summary>
/// Reads stored session progress for the API, scoped to the current user.
/// </summary>
public sealed class SessionProgressReader(
    ISessionProgressRepository repository,
    InstanceTracker instances,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Returns row summaries for whichever of <paramref name="sessionIds"/> have progress.</summary>
    public async Task<IReadOnlyDictionary<string, SessionProgressSummaryDto>> GetSummariesAsync(
        IReadOnlyCollection<string> sessionIds,
        CancellationToken ct)
    {
        var stored = await repository.GetManyAsync(sessionIds, ct).ConfigureAwait(false);
        return stored.ToDictionary(
            entry => entry.Key,
            entry => SessionProgressTracker.ToSummary(entry.Value),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns a session's progress. When none is stored yet (a session from before Fleet tracked progress,
    /// say) and its harness is running and can report a todo list, asks the harness and stores the answer.
    /// </summary>
    public async Task<SessionProgressDto?> GetAsync(Session session, CancellationToken ct)
    {
        var stored = await repository.GetAsync(session.Id, ct).ConfigureAwait(false);
        if (stored is not null)
            return SessionProgressTracker.ToDto(stored);

        var instance = string.IsNullOrEmpty(session.InstanceId) ? null : instances.Get(session.InstanceId);
        if (instance is null)
            return null;

        var todos = await instance.GetTodosAsync(ct).ConfigureAwait(false);
        if (todos is null)
            return null;

        var seeded = SessionProgressTracker.ApplyTodos(null, session.Id, session.UserId, todos, _time.GetUtcNow());
        if (seeded is null)
            return null;

        await repository.UpsertAsync(seeded, ct).ConfigureAwait(false);
        return SessionProgressTracker.ToDto(seeded);
    }
}
