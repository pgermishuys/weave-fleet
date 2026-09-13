using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Progress;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Progress;

/// <summary>
/// Applies the events <see cref="SessionProgressObserver"/> queues, stores each session's progress, and
/// pushes changes: the row summary on the "sessions" topic and the full detail on the session's own topic.
/// </summary>
internal sealed partial class SessionProgressService(
    IServiceScopeFactory scopeFactory,
    SessionProgressObserver observer,
    IEventBroadcaster broadcaster,
    ILogger<SessionProgressService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var observed in observer.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ApplyAsync(observed, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogApplyFailed(observed.SessionId, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Applies one event. Returns the new progress, or <see langword="null"/> when nothing changed.</summary>
    internal async Task<SessionProgress?> ApplyAsync(ObservedProgressEvent observed, CancellationToken ct)
    {
        using var userScope = BackgroundUserContext.BeginScope(observed.UserId);
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>();

        var current = await repository.GetForOwnerAsync(observed.SessionId, observed.UserId, ct).ConfigureAwait(false);
        var next = observed.Event switch
        {
            TodosReported todos => SessionProgressTracker.ApplyTodos(
                current, observed.SessionId, observed.UserId, todos.Payload.Items, observed.At),
            _ => null,
        };

        if (next is null || !await repository.UpsertAsync(next, ct).ConfigureAwait(false))
            return null;

        var summary = JsonSerializer.SerializeToElement(
            SessionProgressTracker.ToSummary(next), InfrastructureJsonContext.Default.SessionProgressSummaryDto);
        await broadcaster.BroadcastAsync("sessions", SessionProgressTracker.SummaryEventType, summary, next.UserId, ct)
            .ConfigureAwait(false);

        var detail = JsonSerializer.SerializeToElement(
            SessionProgressTracker.ToDto(next), InfrastructureJsonContext.Default.SessionProgressDto);
        await broadcaster.BroadcastAsync($"session:{next.SessionId}", SessionProgressTracker.DetailEventType, detail, next.UserId, ct)
            .ConfigureAwait(false);

        return next;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update progress for session {SessionId}.")]
    private partial void LogApplyFailed(string sessionId, Exception ex);
}
