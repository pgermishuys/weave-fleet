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
            FilesWritten written => await ApplyPlanFilesAsync(
                scope.ServiceProvider, current, observed, written.Payload.Paths, written.Payload.MessageId, ct).ConfigureAwait(false),
            // A turn ended: read the plans again, to catch ticks made by a shell command or in an editor.
            SessionIdled when current is { Plans.Count: > 0 } => await ApplyPlanFilesAsync(
                scope.ServiceProvider, current, observed, [.. current.Plans.Select(plan => plan.Path)], messageId: null, ct).ConfigureAwait(false),
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

    /// <summary>
    /// Reads each markdown file from the session's folder and applies what it says. Paths are absolute or relative
    /// to the session's folder. Returns <see langword="null"/> when no plan changed.
    /// </summary>
    private static async Task<SessionProgress?> ApplyPlanFilesAsync(
        IServiceProvider services,
        SessionProgress? current,
        ObservedProgressEvent observed,
        IReadOnlyList<string> paths,
        string? messageId,
        CancellationToken ct)
    {
        var candidates = paths.Where(PlanFileReader.IsMarkdown).ToList();
        if (candidates.Count == 0)
            return null;

        var session = await services.GetRequiredService<ISessionRepository>().GetByIdAsync(observed.SessionId).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(session.Directory))
            return null;

        var progress = current;
        var changed = false;
        foreach (var path in candidates)
        {
            var read = await PlanFileReader.ReadAsync(session.Directory, path, ct).ConfigureAwait(false);
            if (read.Status == PlanFileStatus.Refused)
                continue;

            var document = read.Status == PlanFileStatus.Read ? ChecklistPlanParser.Parse(read.Content!) : null;
            var next = SessionProgressTracker.ApplyPlanFile(
                progress, observed.SessionId, observed.UserId, read.RelativePath!, document, messageId, observed.At);
            if (next is null)
                continue;

            progress = next;
            changed = true;
        }

        return changed ? progress : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update progress for session {SessionId}.")]
    private partial void LogApplyFailed(string sessionId, Exception ex);
}
