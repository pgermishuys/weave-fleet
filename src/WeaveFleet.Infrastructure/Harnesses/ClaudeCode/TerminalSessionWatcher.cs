using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Notices a terminal session that ended. Claude Code usually kills the session's MCP server without closing its input, so
/// the server can't always say so: when its heartbeats stop, the session is over, and its turns are read once more.
/// </summary>
public sealed partial class TerminalSessionWatcher(
    TerminalSessionPresence presence,
    IServiceScopeFactory scopes,
    ILogger<TerminalSessionWatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TerminalSessions.HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await EndSilentAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Fleet is stopping.
        }
    }

    /// <summary>Reads again every session that has gone silent.</summary>
    internal async Task EndSilentAsync(CancellationToken ct)
    {
        foreach (var open in presence.TakeSilent(TerminalSessions.Silence))
        {
            try
            {
                using var asUser = BackgroundUserContext.BeginScope(open.UserId);
                using var scope = scopes.CreateScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ITerminalSessions>();
                await sessions.ReportAsync(new TerminalSessionReport(TerminalSessions.Ended, open.ClaudeSessionId, open.Folder, open.TranscriptPath), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotRead(ex, open.ClaudeSessionId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read terminal session {ClaudeSessionId} after it ended")]
    private partial void LogNotRead(Exception ex, string claudeSessionId);
}
