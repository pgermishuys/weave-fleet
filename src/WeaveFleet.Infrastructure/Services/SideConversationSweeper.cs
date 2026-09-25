using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Deletes side conversations (<c>/btw</c>) whose undo window has passed since they were discarded: the fork in the
/// harness and the Fleet session. On the server, so a discard still happens when the page that asked for it was
/// reloaded or closed, and one brought back with Undo never is. Also picks up any a restart left behind.
/// </summary>
internal sealed partial class SideConversationSweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<SideConversationSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(ex);
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

    internal async Task SweepAsync(CancellationToken ct)
    {
        IReadOnlyList<Domain.Entities.Session> expired;
        using (var scope = scopeFactory.CreateScope())
        {
            var cutoff = (time.GetUtcNow() - SideConversations.DiscardUndoWindow).UtcDateTime.ToString("O");
            expired = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .ListSideConversationsDiscardedBeforeAsync(cutoff).ConfigureAwait(false);
        }

        foreach (var side in expired)
        {
            // As its owner: the orchestrator reads and deletes only the caller's sessions.
            using var user = BackgroundUserContext.BeginScope(side.UserId);
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SessionOrchestrator>()
                .DeleteDiscardedSideConversationAsync(side.Id, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting discarded side conversations failed; trying again shortly")]
    private partial void LogSweepFailed(Exception ex);
}
