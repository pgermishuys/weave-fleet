using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// In-memory <see cref="IAppRunRepository"/>: upserts only into known sessions, and an existing run keeps its
/// session, owner and creation time. It doesn't scope by user; tests that need that use the SQLite repository.
/// </summary>
public sealed class InMemoryAppRunRepository : IAppRunRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AppRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projectBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previewCommandByProject = new(StringComparer.Ordinal);

    /// <summary>
    /// A session that runs can be stored for, in <paramref name="projectDirectory"/> (for remembered preview
    /// commands; each session is its own project unless given one).
    /// </summary>
    public void AddSession(string sessionId, string? projectDirectory = null)
    {
        lock (_gate)
            _projectBySession[sessionId] = projectDirectory ?? sessionId;
    }

    public IReadOnlyList<AppRun> All
    {
        get
        {
            lock (_gate)
                return [.. _runs.Values.Select(Copy)];
        }
    }

    public Task<AppRun?> GetByIdAsync(string sessionId, string appId)
    {
        lock (_gate)
            return Task.FromResult(_runs.TryGetValue(appId, out var run) && run.SessionId == sessionId ? Copy(run) : null);
    }

    public Task<IReadOnlyList<AppRun>> ListBySessionIdAsync(string sessionId)
    {
        lock (_gate)
        {
            IReadOnlyList<AppRun> runs = [.. _runs.Values.Where(run => run.SessionId == sessionId).OrderBy(run => run.CreatedAt, StringComparer.Ordinal).ThenBy(run => run.Id, StringComparer.Ordinal).Select(Copy)];
            return Task.FromResult(runs);
        }
    }

    public Task<bool> UpsertAsync(AppRun run)
    {
        lock (_gate)
        {
            if (!_projectBySession.ContainsKey(run.SessionId))
                return Task.FromResult(false);

            if (_runs.TryGetValue(run.Id, out var existing))
            {
                if (existing.SessionId != run.SessionId || existing.UserId != run.UserId)
                    return Task.FromResult(false);
                var updated = Copy(run);
                updated.CreatedAt = existing.CreatedAt;
                _runs[run.Id] = updated;
            }
            else
            {
                _runs[run.Id] = Copy(run);
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<AppRun>> ListUnfinishedForAllUsersAsync()
    {
        lock (_gate)
        {
            IReadOnlyList<AppRun> runs = [.. _runs.Values.Where(run => run.Status is not ("exited" or "stopped")).Select(Copy)];
            return Task.FromResult(runs);
        }
    }

    public Task MarkStoppedForAllUsersAsync(IReadOnlyCollection<string> appIds, string updatedAt)
    {
        lock (_gate)
        {
            foreach (var appId in appIds)
            {
                if (!_runs.TryGetValue(appId, out var run))
                    continue;
                run.Status = "stopped";
                run.ExitCode = null;
                run.Pid = null;
                run.PidStartedAt = null;
                run.UpdatedAt = updatedAt;
            }
        }
        return Task.CompletedTask;
    }

    public Task RememberPreviewCommandAsync(string sessionId, string command, string updatedAt)
    {
        lock (_gate)
        {
            if (_projectBySession.TryGetValue(sessionId, out var project))
                _previewCommandByProject[project] = command;
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetPreviewCommandAsync(string sessionId)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _projectBySession.TryGetValue(sessionId, out var project) && _previewCommandByProject.TryGetValue(project, out var command)
                    ? command
                    : null);
        }
    }

    private static AppRun Copy(AppRun run) => new()
    {
        Id = run.Id,
        SessionId = run.SessionId,
        UserId = run.UserId,
        Command = run.Command,
        Directory = run.Directory,
        Port = run.Port,
        Status = run.Status,
        ExitCode = run.ExitCode,
        Url = run.Url,
        Pid = run.Pid,
        PidStartedAt = run.PidStartedAt,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
    };
}
