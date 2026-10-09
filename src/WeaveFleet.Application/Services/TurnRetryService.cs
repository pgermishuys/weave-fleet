using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>A session's retry as clients show it: when it goes, which attempt, and why.</summary>
public sealed record ScheduledRetryView(string DueAt, int Attempt, string Kind, string Reason, bool ProviderSaid)
{
    public static ScheduledRetryView From(ScheduledRetry retry) => new(
        retry.DueAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        retry.Attempt,
        retry.Kind,
        retry.Reason,
        retry.ProviderSaid);
}

/// <summary>The <c>session.retry</c> event: the session's retry now, or <see langword="null"/> when it has none waiting.</summary>
public sealed record ScheduledRetryChanged(
    string SessionId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] ScheduledRetryView? Retry);

/// <summary>
/// Tries a turn a model provider's limit stopped again, by itself: a rate limit, a subscription's usage limit, an
/// overload. It sends "Continue where you left off." when the limit resets, if the provider said when, or after a
/// wait (<see cref="TurnRetryPolicy"/>). The retry is kept in Fleet's database and shown on the failure card and the
/// session's row, with Try now and Don't retry. Every change goes out as <see cref="ChangedEvent"/> on the session's
/// topic and the sessions topic.
/// </summary>
/// <remarks>
/// While a retry waits, the session's queue waits with it (<see cref="TurnRetryScheduler.IsHolding"/>): what the user
/// queued comes after the work the limit cut off. A turn the user starts meanwhile replaces the retry. The
/// "Try again when a limit resets" setting (<see cref="PreferenceKey"/>) turns it off.
/// </remarks>
public sealed partial class TurnRetryService(
    SessionOrchestrator orchestrator,
    PromptQueueService queue,
    ISessionRepository sessions,
    IScheduledRetryRepository retries,
    IUserPreferenceRepository preferences,
    IEventBroadcaster broadcaster,
    SessionActivityTracker activity,
    TurnRetryScheduler scheduler,
    IUserContext userContext,
    TimeProvider time,
    ILogger<TurnRetryService> logger)
{
    /// <summary>The event carrying a session's retry after it changed (<see cref="ScheduledRetryChanged"/>).</summary>
    public const string ChangedEvent = "session.retry";

    /// <summary>The Settings switch: on unless the user turned it off.</summary>
    public const string PreferenceKey = "RetryAfterLimits";

    /// <summary>What Fleet sends when it tries again.</summary>
    public const string ContinueText = "Continue where you left off.";

    public async Task<Result<ScheduledRetry?>> GetAsync(string sessionId)
    {
        if (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false) is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);
        var retry = await retries.GetAsync(sessionId).ConfigureAwait(false);
        return retry is { State: ScheduledRetryStates.Waiting } ? retry : (ScheduledRetry?)null;
    }

    /// <summary>
    /// A limit stopped the session's turn: schedules the next attempt. When Fleet won't try again (the setting is off,
    /// the attempts are used up, the session is archived), the queue the failure held goes out as it would have.
    /// </summary>
    public async Task ScheduleAsync(string sessionId, TurnError error, CancellationToken ct = default)
    {
        var previous = await retries.GetAsync(sessionId).ConfigureAwait(false);
        var attempt = (previous?.Attempt ?? 0) + 1;
        var now = time.GetUtcNow();
        var plan = await IsEnabledAsync().ConfigureAwait(false)
            && await sessions.GetByIdAsync(sessionId).ConfigureAwait(false) is { RetentionStatus: not "archived" }
                ? TurnRetryPolicy.Plan(error, attempt, now)
                : null;

        if (plan is not { } next)
        {
            if (previous is not null)
                await retries.RemoveAsync(sessionId).ConfigureAwait(false);
            scheduler.Forget(sessionId);
            await queue.SendNextAsync(sessionId, ct).ConfigureAwait(false);
            return;
        }

        var retry = new ScheduledRetry
        {
            SessionId = sessionId,
            UserId = userContext.UserId,
            DueAt = next.DueAt,
            Attempt = attempt,
            Kind = error.Kind!,
            Reason = error.Message,
            ProviderSaid = next.ProviderSaid,
            State = ScheduledRetryStates.Waiting,
            CreatedAt = now,
        };
        if (!await retries.SaveAsync(retry).ConfigureAwait(false))
        {
            scheduler.Forget(sessionId);
            return;
        }

        scheduler.Track(retry);
        LogScheduled(sessionId, attempt, retry.Kind, retry.DueAt);
        await BroadcastAsync(sessionId, retry, ct).ConfigureAwait(false);
    }

    /// <summary>Don't retry: the user takes it from here. What they queued goes out now.</summary>
    public async Task<Result<Unit>> CancelAsync(string sessionId, CancellationToken ct = default)
    {
        var removed = await retries.RemoveAsync(sessionId).ConfigureAwait(false);
        scheduler.Forget(sessionId);
        if (removed is not { State: ScheduledRetryStates.Waiting })
            return FleetError.NotFoundFor(nameof(ScheduledRetry), sessionId);

        await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
        if (!IsInTurn(sessionId))
            await queue.SendNextAsync(sessionId, ct).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>Try now rather than at the due time.</summary>
    public async Task<Result<Unit>> SendNowAsync(string sessionId, CancellationToken ct = default)
    {
        if (IsInTurn(sessionId))
            return FleetError.ValidationError("Retry.Session", "The session is working. Fleet's retry is no longer needed.");
        return await SendAsync(sessionId, ct).ConfigureAwait(false)
            ? Unit.Value
            : FleetError.NotFoundFor(nameof(ScheduledRetry), sessionId);
    }

    /// <summary>The retry is due: sends it, unless the session is busy, which pushes it back a minute.</summary>
    public async Task FireAsync(string sessionId, CancellationToken ct = default)
    {
        if (IsInTurn(sessionId))
        {
            if (await retries.GetAsync(sessionId).ConfigureAwait(false) is { State: ScheduledRetryStates.Waiting } waiting)
            {
                var later = waiting with { DueAt = time.GetUtcNow().AddMinutes(1) };
                if (await retries.SaveAsync(later).ConfigureAwait(false))
                    scheduler.Track(later);
            }
            return;
        }

        if (await IsEnabledAsync().ConfigureAwait(false))
        {
            await SendAsync(sessionId, ct).ConfigureAwait(false);
            return;
        }

        // Turned off since: it's the user's again.
        await CancelAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>A turn started: one the user started replaces a waiting retry; Fleet's own was marked sent first.</summary>
    public async Task OnTurnStartedAsync(string sessionId, CancellationToken ct = default)
    {
        if (await retries.GetAsync(sessionId).ConfigureAwait(false) is not { State: ScheduledRetryStates.Waiting })
            return;

        await retries.RemoveAsync(sessionId).ConfigureAwait(false);
        scheduler.Forget(sessionId);
        await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
    }

    /// <summary>A turn ended without a limit stopping it: the attempts start again from the first next time.</summary>
    public async Task OnTurnEndedAsync(string sessionId)
    {
        if (await retries.GetAsync(sessionId).ConfigureAwait(false) is { State: ScheduledRetryStates.Sent })
            await retries.RemoveAsync(sessionId).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(string sessionId, CancellationToken ct)
    {
        var retry = await retries.TakeWaitingAsync(sessionId).ConfigureAwait(false);
        scheduler.Forget(sessionId);
        if (retry is null)
            return false;

        await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
        LogSending(sessionId, retry.Attempt);
        var sent = await orchestrator.PromptSessionAsync(
            sessionId,
            ContinueText,
            new PromptOptions { Delivery = PromptDelivery.Queue },
            ct).ConfigureAwait(false);
        if (sent.IsSuccess)
            return true;

        // The session couldn't take it (its harness didn't start): that's one attempt, and the next waits longer.
        LogSendFailed(sessionId, sent.Error.Description);
        await ScheduleAsync(
            sessionId,
            new TurnError { Name = "RetryFailed", Message = retry.Reason, Kind = retry.Kind },
            ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsEnabledAsync()
        => !string.Equals(await preferences.GetAsync(PreferenceKey).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Busy, stopped on a question, or waiting on a delegated session: the turn hasn't ended.</summary>
    private bool IsInTurn(string sessionId)
    {
        var status = activity.Get(sessionId)?.ActivityStatus;
        return SessionActivityTracker.IsInTurn(status) || status == ActivityStatuses.Delegating;
    }

    private async Task BroadcastAsync(string sessionId, ScheduledRetry? retry, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new ScheduledRetryChanged(sessionId, retry is null ? null : ScheduledRetryView.From(retry)),
            ApplicationJsonContext.Default.ScheduledRetryChanged);
        // The session's topic for its failure card; the sessions topic for its row in the list.
        await broadcaster.BroadcastAsync($"session:{sessionId}", ChangedEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
        await broadcaster.BroadcastAsync("sessions", ChangedEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A limit stopped session {SessionId}'s turn: Fleet tries again (attempt {Attempt}, {Kind}) at {DueAt}")]
    private partial void LogScheduled(string sessionId, int attempt, string kind, DateTimeOffset dueAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trying session {SessionId} again (attempt {Attempt})")]
    private partial void LogSending(string sessionId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not try session {SessionId} again: {Reason}")]
    private partial void LogSendFailed(string sessionId, string reason);
}
