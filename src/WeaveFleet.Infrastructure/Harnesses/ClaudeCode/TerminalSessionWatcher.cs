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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Following> _following = new(StringComparer.Ordinal);

    /// <summary>Watches one open session's transcript, so its new turns show as they are written.</summary>
    private sealed class Following(FileSystemWatcher files, TerminalSessionPresence.Open open) : IDisposable
    {
        private int _pending;
        public TerminalSessionPresence.Open Open { get; } = open;

        /// <summary>True when no read is already waiting: changes that come in a burst are read once.</summary>
        public bool TryQueue() => Interlocked.Exchange(ref _pending, 1) == 0;

        public void Done() => Interlocked.Exchange(ref _pending, 0);

        public void Dispose() => files.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        presence.Opened += Follow;
        presence.Closed += Unfollow;
        foreach (var open in presence.OpenSessions())
            Follow(open);

        using var timer = new PeriodicTimer(TerminalSessions.HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var open in presence.OpenSessions())
                    Follow(open);
                await EndSilentAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Fleet is stopping.
        }
        finally
        {
            presence.Opened -= Follow;
            presence.Closed -= Unfollow;
            foreach (var id in _following.Keys)
                Unfollow(id);
        }
    }

    private void Follow(TerminalSessionPresence.Open open)
    {
        if (_following.ContainsKey(open.ClaudeSessionId))
            return;

        // The server starts before anything is typed, so it often has no path yet: the transcript goes in the folder's own
        // project folder under Claude Code's, which exists (or not yet) before the file does. If the folder isn't there yet,
        // the next tick looks again and the heartbeat reads new turns meanwhile.
        var directory = open.TranscriptPath is { Length: > 0 } path && ClaudeTranscriptLocator.IsTranscriptOf(path, open.ClaudeSessionId)
            ? Path.GetDirectoryName(path)
            : Path.Combine(ClaudeTranscriptLocator.ConfigDirectory(), "projects", ClaudeTranscriptLocator.Slug(open.Folder));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        try
        {
            var files = new FileSystemWatcher(directory, open.ClaudeSessionId + ".jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            var following = new Following(files, open);
            files.Changed += (_, _) => Changed(following);
            files.Created += (_, _) => Changed(following);
            files.EnableRaisingEvents = true;
            if (_following.TryAdd(open.ClaudeSessionId, following))
                return;
            following.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogNotWatched(ex, open.ClaudeSessionId);
        }
    }

    private void Unfollow(string claudeSessionId)
    {
        if (_following.TryRemove(claudeSessionId, out var following))
            following.Dispose();
    }

    private void Changed(Following following)
    {
        if (!following.TryQueue())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Claude Code writes a reply in several lines: wait for a pause in them.
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                following.Done();
                var open = following.Open;
                using var asUser = BackgroundUserContext.BeginScope(open.UserId);
                using var scope = scopes.CreateScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ITerminalSessions>();
                await sessions.ReportAsync(new TerminalSessionReport(TerminalSessions.Changed, open.ClaudeSessionId, open.Folder, open.TranscriptPath), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                following.Done();
                LogNotRead(ex, following.Open.ClaudeSessionId);
            }
        });
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't watch the transcript of terminal session {ClaudeSessionId}; its heartbeat will pick up new turns")]
    private partial void LogNotWatched(Exception ex, string claudeSessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read terminal session {ClaudeSessionId} after it ended")]
    private partial void LogNotRead(Exception ex, string claudeSessionId);
}
