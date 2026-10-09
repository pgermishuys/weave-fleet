using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Keeps each failed turn's failure (<see cref="TurnFailed"/>) as a message of Fleet's, so the conversation still says
/// why the turn stopped after a reload. A turn that fails before the model answers leaves nothing in the harness's
/// history; the session's snapshot places the kept failure where it happened
/// (<see cref="MessagePersistenceService.CreateTurnFailureMessage"/>). The relay hands it every event it translates.
/// </summary>
public sealed partial class TurnFailureRecorder(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<TurnFailureRecorder> logger)
{
    private readonly Lock _gate = new();
    private readonly List<Task> _inFlight = [];

    /// <summary>Finished when every failure seen so far is saved. Tests wait on it.</summary>
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
        if (domainEvent is not TurnFailed failed || string.IsNullOrEmpty(userId))
            return;

        var at = time.GetUtcNow();
        var save = Task.Run(() => SaveAsync(sessionId, userId, failed.Payload.Error, at));
        lock (_gate)
            _inFlight.Add(save);
        _ = save.ContinueWith(
            done =>
            {
                lock (_gate)
                    _inFlight.Remove(done);
            },
            TaskScheduler.Default);
    }

    private async Task SaveAsync(string sessionId, string userId, TurnError error, DateTimeOffset at)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            var message = MessagePersistenceService.CreateTurnFailureMessage(error, at);
            await scope.ServiceProvider.GetRequiredService<IMessageRepository>()
                .UpsertAsync(MessagePersistenceService.ToPersistedMessage(sessionId, message))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSaveFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep the failure of a turn of session {SessionId}")]
    private partial void LogSaveFailed(Exception ex, string sessionId);
}
