using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Takes what every harness reports about its session's context window from the relay to
/// <see cref="SessionContextService"/>: each model call's size and the model's limits, compactions, and the end of each
/// turn (<see cref="SessionIdled"/>), when the context's size goes on the session's list of turns.
/// </summary>
/// <remarks>
/// Each session's reports are handled one at a time in the order its harness sent them, off the relay's pump: a turn's
/// end handled before its last call would leave that call off the list.
/// </remarks>
public sealed partial class SessionContextRecorder(IServiceScopeFactory scopeFactory, ILogger<SessionContextRecorder> logger)
{
    private readonly ConcurrentDictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>A model call's size, or the model's limits, for session <paramref name="sessionId"/>.</summary>
    public void Observe(string sessionId, string? userId, ContextUsageReport report)
        => Queue(sessionId, userId, (service, ct) => service.RecordUsageAsync(sessionId, report, ct));

    /// <summary>A compaction of session <paramref name="sessionId"/>'s context started, ended or failed.</summary>
    public void Observe(string sessionId, string? userId, ContextCompactionReport report)
        => Queue(sessionId, userId, (service, ct) => service.RecordCompactionAsync(sessionId, report, ct));

    /// <summary>Called for every event the relay translates, on its pump: a turn's end is the one it acts on.</summary>
    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (domainEvent is SessionIdled)
            Queue(sessionId, userId, (service, ct) => service.RecordTurnEndedAsync(sessionId, ct));
    }

    /// <summary>Completes when every report queued for the session so far is handled. Tests wait on it.</summary>
    internal Task Idle(string sessionId) => _tails.TryGetValue(sessionId, out var tail) ? tail : Task.CompletedTask;

    private void Queue(string sessionId, string? userId, Func<SessionContextService, CancellationToken, Task> handle)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (_sync)
        {
            var tail = _tails.GetValueOrDefault(sessionId) ?? Task.CompletedTask;
            _tails[sessionId] = tail
                .ContinueWith(_ => RunAsync(sessionId, userId, handle), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task RunAsync(string sessionId, string userId, Func<SessionContextService, CancellationToken, Task> handle)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            await handle(scope.ServiceProvider.GetRequiredService<SessionContextService>(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record the context of session {SessionId}")]
    private partial void LogFailed(Exception ex, string sessionId);
}
