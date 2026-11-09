using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>Reads the session's changed files as they are now, or null when the folder has no diff to read.</summary>
public interface ITurnFileStatsSource
{
    Task<IReadOnlyList<TurnFileStat>?> ReadAsync(string sessionId, CancellationToken ct);
}

/// <summary>The session's diff against its baseline (the Changes panel's numbers).</summary>
public sealed class GitTurnFileStatsSource(ISessionRepository sessions, GitDiffService git) : ITurnFileStatsSource
{
    public async Task<IReadOnlyList<TurnFileStat>?> ReadAsync(string sessionId, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null || await SessionDiffScope.ResolveAsync(session, git, ct).ConfigureAwait(false) is not { } scope)
            return null;

        var diffs = await git.ComputeDiffsWithAvailabilityAsync(scope.RepoRoot, scope.Base.Ref, scope.WorkspacePrefix, ct).ConfigureAwait(false);
        return diffs.Available
            ? [.. diffs.Diffs.Select(diff => new TurnFileStat(diff.Path, diff.AddedLines ?? 0, diff.DeletedLines ?? 0))]
            : null;
    }
}

/// <summary>
/// Keeps what the session's files changed by as each turn ends. A whole-file write sends only the new contents, so a
/// card can't say what the write changed; the file's change against the session's baseline at the turn's end is the
/// answer, and later turns change the same file. Kept on the server so every browser, phone and machine shows the same
/// counts for a turn. The relay hands it every event it translates; a turn ends at <see cref="SessionIdled"/>.
/// </summary>
public sealed partial class TurnFileStatsRecorder(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<TurnFileStatsRecorder> logger)
{
    private readonly Lock _gate = new();
    private readonly List<Task> _inFlight = [];

    /// <summary>Finished when every turn end seen so far is saved. Tests wait on it.</summary>
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

        var at = time.GetUtcNow();
        var save = Task.Run(() => SaveAsync(sessionId, userId, at));
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

    private async Task SaveAsync(string sessionId, string userId, DateTimeOffset at)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            var files = await scope.ServiceProvider.GetRequiredService<ITurnFileStatsSource>().ReadAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (files is null)
                return;

            await scope.ServiceProvider.GetRequiredService<ITurnFileStatsRepository>().AddAsync(sessionId, at, files).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSaveFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep the files changed as a turn of session {SessionId} ended")]
    private partial void LogSaveFailed(Exception ex, string sessionId);
}
