using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Keeps Fleet's record of how full each session's context window is, from what its harness reports
/// (<see cref="ContextUsageReport"/>, <see cref="ContextCompactionReport"/>), and tells the session's open clients
/// whenever it changes (<see cref="SessionContextUsage.UpdatedEventType"/>). Scoped, as the session's owner.
/// </summary>
public sealed class SessionContextService(
    ISessionContextRepository repository,
    IUserContext userContext,
    IEventBroadcaster broadcaster,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>The current user's record of the session's context, or null when its harness hasn't reported any.</summary>
    public async Task<SessionContextUsage?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var context = await repository.GetAsync(sessionId, ct).ConfigureAwait(false);
        return context is null ? null : SessionContextUsage.From(context);
    }

    /// <summary>
    /// A model call's size, or the model's limits. A call replaces the last one; limits the report doesn't bring are
    /// kept while the model stays the same.
    /// </summary>
    public Task RecordUsageAsync(string sessionId, ContextUsageReport report, CancellationToken ct = default)
        => UpdateAsync(sessionId, current =>
        {
            var now = _time.GetUtcNow();
            var sameModel = report.ModelId is null || current.ModelId is null
                || string.Equals(report.ModelId, current.ModelId, StringComparison.Ordinal);
            var next = current with
            {
                ModelId = report.ModelId ?? current.ModelId,
                ProviderId = report.ProviderId ?? current.ProviderId,
                Limit = report.Limit ?? (sameModel ? current.Limit : null),
                CompactsAt = report.CompactsAt ?? (sameModel ? current.CompactsAt : null),
            };

            if (report.Call is { } call)
            {
                next = next with
                {
                    Used = call.Used,
                    LastCall = call,
                    LastCallAt = now,
                    // A call after a compaction means it's over, whether or not the harness said so.
                    Compacting = false,
                };
            }

            return next == current ? null : next with { UpdatedAt = now };
        }, ct);

    /// <summary>
    /// A compaction started, ended or failed. Once it ends, the context's size is unknown until the next call
    /// measures it: the harnesses only estimate it, or report the summarising call's tokens instead.
    /// </summary>
    public Task RecordCompactionAsync(string sessionId, ContextCompactionReport report, CancellationToken ct = default)
        => UpdateAsync(sessionId, current =>
        {
            var now = _time.GetUtcNow();
            return report.Phase switch
            {
                ContextCompactionPhases.Started => current with { Compacting = true, CompactionError = null, UpdatedAt = now },
                ContextCompactionPhases.Ended => current with
                {
                    Compacting = false,
                    CompactedAt = now,
                    CompactionError = null,
                    Used = null,
                    LastCall = null,
                    LastCallAt = null,
                    UpdatedAt = now,
                },
                ContextCompactionPhases.Failed => current with
                {
                    Compacting = false,
                    CompactionError = string.IsNullOrWhiteSpace(report.Error) ? "The compaction failed." : report.Error,
                    UpdatedAt = now,
                },
                _ => null,
            };
        }, ct);

    /// <summary>
    /// The session's turn ended: the context's size after its last call goes on the list of turns. A turn with no
    /// call since the last one (it failed at once, or only compacted) adds nothing. A compaction still under way is
    /// over: compacting is a turn of its own, and a harness that doesn't say how it ended (OpenCode, when it fails)
    /// would otherwise leave it running for good.
    /// </summary>
    public Task RecordTurnEndedAsync(string sessionId, CancellationToken ct = default)
        => UpdateAsync(sessionId, current =>
        {
            var next = current with { Compacting = false };
            var last = current.Turns.Count > 0 ? current.Turns[^1] : null;
            if (current is { Used: { } used, LastCallAt: { } callAt } && (last is null || last.At < callAt))
            {
                var afterCompaction = current.CompactedAt is { } compactedAt && (last is null || compactedAt > last.At);
                var turns = current.Turns.Append(new SessionContextTurn(used, current.Limit, _time.GetUtcNow(), afterCompaction))
                    .TakeLast(SessionContext.MaxTurns)
                    .ToList();
                next = next with { Turns = turns };
            }

            return next == current ? null : next with { UpdatedAt = _time.GetUtcNow() };
        }, ct);

    /// <summary>
    /// Applies <paramref name="change"/> to the session's record (a blank one when there's none yet), stores it and
    /// tells clients. A change that returns null changes nothing.
    /// </summary>
    private async Task UpdateAsync(string sessionId, Func<SessionContext, SessionContext?> change, CancellationToken ct)
    {
        var userId = userContext.UserId;
        var current = await repository.GetForOwnerAsync(sessionId, userId, ct).ConfigureAwait(false)
            ?? new SessionContext { SessionId = sessionId, UserId = userId };
        if (change(current) is not { } next)
            return;

        if (!await repository.UpsertAsync(next, ct).ConfigureAwait(false))
            return;

        var payload = JsonSerializer.SerializeToElement(SessionContextUsage.From(next), ApplicationJsonContext.Default.SessionContextUsage);
        await broadcaster.BroadcastAsync($"session:{sessionId}", SessionContextUsage.UpdatedEventType, payload, userId, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
