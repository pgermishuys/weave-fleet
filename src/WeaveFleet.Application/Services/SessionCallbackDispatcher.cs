using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Drives <see cref="SessionCallbackService"/>. The relay hands it every event it translates: a session's first
/// reply of a turn marks the callbacks it's the source of as started, and its idle event fires them. The poll
/// (<see cref="SessionCallbackPoller"/>) catches what no event will: a turn a Fleet restart ended, a target that was
/// busy, a delivery that failed.
/// </summary>
/// <remarks>
/// Everything runs one at a time, in the order it came, off the relay's pump and as the sessions' owner. So the
/// started mark is saved before the idle event that follows it is handled, and a callback is never delivered twice by
/// an idle event and the poll at once.
/// </remarks>
public sealed partial class SessionCallbackDispatcher(IServiceScopeFactory scopeFactory, ILogger<SessionCallbackDispatcher> logger)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _working = new(StringComparer.Ordinal);
    private Task _tail = Task.CompletedTask;

    /// <summary>Finished when everything handed to the dispatcher so far has run. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return _tail;
        }
    }

    /// <summary>Called for every event the relay translates, on its pump.</summary>
    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        switch (domainEvent)
        {
            case MessageCreated { Payload.Info.Role: "assistant" }:
            case MessageUpdated { Payload.Info.Role: "assistant" }:
                // Once a turn: a reply streams many updates.
                lock (_gate)
                {
                    if (!_working.Add(sessionId))
                        return;
                }

                Enqueue(userId, (callbacks, _) => callbacks.MarkSourceStartedAsync(sessionId), CancellationToken.None);
                break;
            case SessionIdled:
                lock (_gate)
                    _working.Remove(sessionId);

                Enqueue(userId, (callbacks, ct) => callbacks.OnSessionIdledAsync(sessionId, ct), CancellationToken.None);
                break;
        }
    }

    /// <summary>Fires every started callback that's ready, for each user who has one.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        IReadOnlyList<string> owners;
        using (var scope = scopeFactory.CreateScope())
            owners = await scope.ServiceProvider.GetRequiredService<ISessionCallbackRepository>().GetOwnersWithStartedCallbacksAsync().ConfigureAwait(false);

        foreach (var owner in owners)
            Enqueue(owner, (callbacks, token) => callbacks.ProcessPendingCallbacksAsync(token), ct);

        await Pending.ConfigureAwait(false);
    }

    private void Enqueue(string userId, Func<SessionCallbackService, CancellationToken, Task> work, CancellationToken ct)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(
                    _ => RunAsync(userId, work, ct),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task RunAsync(string userId, Func<SessionCallbackService, CancellationToken, Task> work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            await work(scope.ServiceProvider.GetRequiredService<SessionCallbackService>(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, userId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session completion callbacks failed for user {UserId}")]
    private partial void LogRunFailed(Exception ex, string userId);
}

/// <summary>
/// Fires session completion callbacks that no event will: at startup (a source whose turn a restart ended) and every
/// <see cref="Interval"/> after (a target that was busy, a delivery that failed).
/// </summary>
public sealed partial class SessionCallbackPoller(SessionCallbackDispatcher dispatcher, ILogger<SessionCallbackPoller> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await dispatcher.ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling session completion callbacks failed")]
    private partial void LogPollFailed(Exception ex);
}
