using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Sessions;

/// <summary>
/// Deletes the attached files of sessions archived more than <see cref="Retention"/> ago, once at startup and then every
/// <see cref="Interval"/>. The conversation stays; the files in the session's folder don't.
/// </summary>
internal sealed partial class ArchivedAttachmentsCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<ArchivedAttachmentsCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await SweepAsync();
                if (deleted > 0)
                    LogDeleted(deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Returns how many sessions' files went.</summary>
    internal async Task<int> SweepAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var cutoff = (time.GetUtcNow() - Retention).UtcDateTime.ToString("O");
        var expired = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
            .ListArchivedBeforeAsync(cutoff).ConfigureAwait(false);

        var deleted = 0;
        foreach (var session in expired)
        {
            try
            {
                if (SessionAttachments.DeleteFor(session))
                    deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSessionFailed(ex, session.Id);
            }
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the attachments of {Count} session(s) archived over 30 days ago")]
    private partial void LogDeleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the attachments of archived sessions failed; trying again later")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the attachments of archived session {SessionId}")]
    private partial void LogSessionFailed(Exception ex, string sessionId);
}
