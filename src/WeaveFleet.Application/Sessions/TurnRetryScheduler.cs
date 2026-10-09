using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Drives <see cref="TurnRetryService"/>. The relay hands it every event it translates: a turn a limit stopped
/// (<see cref="TurnFailed"/> with a <see cref="TurnError.Kind"/>) is scheduled, a turn the user starts replaces a
/// waiting retry, and a turn that ends well starts the attempts over. It keeps the waiting retries in memory, loaded
/// from the database when Fleet starts, and sends each when it's due, checking every <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// A session's events are handled one at a time, in the order they came, off the relay's pump and as the session's
/// owner. The hold on the session's queue (<see cref="IsHolding"/>) is taken on the pump itself, before the idle that
/// follows the failure reaches <see cref="PromptQueueDispatcher"/>.
/// </remarks>
public sealed partial class TurnRetryScheduler(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<TurnRetryScheduler> logger)
    : BackgroundService
{
    /// <summary>How often due retries are looked for.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ScheduledRetry> _waiting = new(StringComparer.Ordinal);

    // Sessions whose turn a limit just stopped, until the retry is saved or turned down.
    private readonly ConcurrentDictionary<string, byte> _limited = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);

    /// <summary>Finished when everything handed to the scheduler so far has run. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_tails.Values);
        }
    }

    /// <summary>
    /// Whether the session's queue waits: a retry is waiting, or a limit just stopped its turn and the retry is on
    /// its way.
    /// </summary>
    public bool IsHolding(string sessionId) => _waiting.ContainsKey(sessionId) || _limited.ContainsKey(sessionId);

    /// <summary>Called for every event the relay translates, on its pump.</summary>
    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        switch (domainEvent)
        {
            case TurnFailed { Payload.Error: { } error } when TurnErrorKinds.IsKnown(error.Kind):
                _limited[sessionId] = 0;
                Enqueue(sessionId, userId, (retries, ct) => retries.ScheduleAsync(sessionId, error, ct));
                break;
            case TurnFailed:
                // Something else stopped it: Fleet doesn't try that again, and the attempts start over.
                Enqueue(sessionId, userId, (retries, _) => retries.OnTurnEndedAsync(sessionId));
                break;
            case TurnStarted:
                Enqueue(sessionId, userId, (retries, ct) => retries.OnTurnStartedAsync(sessionId, ct));
                break;
            case SessionIdled:
                Enqueue(sessionId, userId, (retries, _) => retries.OnTurnEndedAsync(sessionId));
                break;
        }
    }

    /// <summary>Keeps <paramref name="retry"/> until it's due.</summary>
    internal void Track(ScheduledRetry retry)
    {
        _waiting[retry.SessionId] = retry;
        _limited.TryRemove(retry.SessionId, out _);
    }

    /// <summary>The session has no retry waiting any more.</summary>
    internal void Forget(string sessionId)
    {
        _waiting.TryRemove(sessionId, out _);
        _limited.TryRemove(sessionId, out _);
    }

    /// <summary>Sends every retry that's due.</summary>
    internal void SendDue()
    {
        var now = time.GetUtcNow();
        foreach (var (sessionId, retry) in _waiting)
        {
            if (retry.DueAt > now)
                continue;

            // Not sent twice while this one is on its way; FireAsync tracks it again if it has to wait.
            _waiting.TryRemove(new KeyValuePair<string, ScheduledRetry>(sessionId, retry));
            _limited[sessionId] = 0;
            Enqueue(sessionId, retry.UserId, async (retries, ct) =>
            {
                try
                {
                    await retries.FireAsync(sessionId, ct).ConfigureAwait(false);
                }
                finally
                {
                    _limited.TryRemove(sessionId, out _);
                }
            });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            foreach (var retry in await scope.ServiceProvider.GetRequiredService<IScheduledRetryRepository>().ListWaitingAsync().ConfigureAwait(false))
                _waiting.TryAdd(retry.SessionId, retry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLoadFailed(ex);
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            SendDue();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Enqueue(string sessionId, string userId, Func<TurnRetryService, CancellationToken, Task> work)
    {
        lock (_gate)
        {
            var tail = _tails.GetValueOrDefault(sessionId) ?? Task.CompletedTask;
            Task next = null!;
            next = tail.ContinueWith(
                    _ => RunAsync(sessionId, userId, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(
                    _ =>
                    {
                        lock (_gate)
                        {
                            if (_tails.GetValueOrDefault(sessionId) == next)
                                _tails.Remove(sessionId);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            _tails[sessionId] = next;
        }
    }

    private async Task RunAsync(string sessionId, string userId, Func<TurnRetryService, CancellationToken, Task> work)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            await work(scope.ServiceProvider.GetRequiredService<TurnRetryService>(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A retry that couldn't be saved mustn't hold the session's queue for good.
            _limited.TryRemove(sessionId, out _);
            LogRunFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load the retries Fleet had waiting")]
    private partial void LogLoadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying a limited turn of session {SessionId} failed")]
    private partial void LogRunFailed(Exception ex, string sessionId);
}
