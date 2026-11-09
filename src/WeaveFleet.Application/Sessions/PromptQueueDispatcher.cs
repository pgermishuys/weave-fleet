using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Sends a session's next queued message (<see cref="PromptQueueService"/>) when its turn ends. The relay hands it
/// every event it translates; <see cref="SessionIdled"/> is the one it acts on, off the relay's pump and as the
/// session's owner.
/// </summary>
/// <remarks>
/// The idle event is the signal, so the send doesn't check the activity tracker again: the tracker may not have
/// caught up with the event yet. What's queued is in the database, so a Fleet restart keeps it; it's sent the next
/// time the session's turn ends, or with Send now.
/// </remarks>
public sealed partial class PromptQueueDispatcher(IServiceScopeFactory scopeFactory, ILogger<PromptQueueDispatcher> logger, ModTurnHook? modTurns = null, DreamFinisher? dreams = null)
{
    private readonly Lock _gate = new();
    private readonly List<Task> _inFlight = [];

    // session id → what to do if the follow-up claimed at its idle turns out not to start a turn
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<Task>> _followUps = new(StringComparer.Ordinal);

    /// <summary>Finished when every send in flight has been made. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_inFlight);
        }
    }

    /// <summary>
    /// Called by the relay before it shows a session's idle. When the event is an idle with a message queued, Fleet is
    /// about to start the next turn, so the session is claimed as following up: the relay shows it as still working and
    /// stays quiet about a finish. If the send then starts no turn (refused, or only shell commands), <paramref name="released"/>
    /// runs so the relay can show the idle it held back.
    /// </summary>
    public async Task<bool> ClaimFollowUpAsync(string sessionId, string? userId, DomainEvent? domainEvent, Func<Task> released)
    {
        if (domainEvent is not SessionIdled || string.IsNullOrEmpty(userId))
            return false;

        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            if (!await scope.ServiceProvider.GetRequiredService<PromptQueueService>().HasFollowUpAsync(sessionId).ConfigureAwait(false))
                return false;
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, sessionId);
            return false;
        }

        _followUps[sessionId] = released;
        return true;
    }

    /// <summary>Called for every event the relay translates, on its pump.</summary>
    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (!string.IsNullOrEmpty(userId))
            modTurns?.Observe(sessionId, domainEvent);

        // Ends a dream whose session just finished its turn; with none running this is a dictionary lookup.
        dreams?.Observe(sessionId, userId, domainEvent);

        if (domainEvent is not SessionIdled || string.IsNullOrEmpty(userId))
            return;

        var send = Task.Run(() => SendNextAsync(sessionId, userId));
        lock (_gate)
            _inFlight.Add(send);
        _ = send.ContinueWith(
            done =>
            {
                lock (_gate)
                    _inFlight.Remove(done);
            },
            TaskScheduler.Default);
    }

    private async Task SendNextAsync(string sessionId, string userId)
    {
        var started = false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            // The user's mods hear the turn ended first; one may queue a follow-up, which the send below then sends.
            if (modTurns is not null)
                await CompleteTurnAsync(scope.ServiceProvider, sessionId, userId).ConfigureAwait(false);
            started = await scope.ServiceProvider.GetRequiredService<PromptQueueService>().SendNextAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, sessionId);
        }

        // The idle was held back for a follow-up that didn't start: show it now.
        if (!started && _followUps.TryRemove(sessionId, out var released))
        {
            try
            {
                await released().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSendFailed(ex, sessionId);
            }
        }
        else if (started)
        {
            _followUps.TryRemove(sessionId, out _);
        }
    }

    private async Task CompleteTurnAsync(IServiceProvider services, string sessionId, string userId)
    {
        try
        {
            var session = await services.GetRequiredService<ISessionRepository>().GetByIdAsync(sessionId).ConfigureAwait(false);
            await modTurns!.CompleteAsync(userId, sessionId, session?.Directory, session?.SideOfSessionId is not null || session?.ParentSessionId is not null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the next queued message to session {SessionId}")]
    private partial void LogSendFailed(Exception ex, string sessionId);
}
