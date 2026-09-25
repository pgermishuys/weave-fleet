using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Services;

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
public sealed partial class PromptQueueDispatcher(IServiceScopeFactory scopeFactory, ILogger<PromptQueueDispatcher> logger)
{
    private readonly Lock _gate = new();
    private readonly List<Task> _inFlight = [];

    /// <summary>Finished when every send in flight has been made. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_inFlight);
        }
    }

    /// <summary>Called for every event the relay translates, on its pump.</summary>
    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            await scope.ServiceProvider.GetRequiredService<PromptQueueService>().SendNextAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the next queued message to session {SessionId}")]
    private partial void LogSendFailed(Exception ex, string sessionId);
}
