namespace WeaveFleet.Application.Browser;

public enum AppRunStatus
{
    /// <summary>The process is up, but nothing on it answers HTTP yet.</summary>
    Starting,

    /// <summary>A page answers.</summary>
    Running,

    /// <summary>The process ended, on its own or because it was stopped.</summary>
    Exited,
}

/// <summary>
/// A command Fleet runs for a session, usually a dev server. <see cref="Url"/> is the page Fleet found,
/// <see cref="Ports"/> every TCP port the process tree listens on, and <see cref="PrintedUrls"/> the local
/// addresses it printed, in order.
/// </summary>
public sealed record AppRunSnapshot(
    string Id,
    string SessionId,
    string Command,
    string Directory,
    AppRunStatus Status,
    int? ExitCode,
    string? Url,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> PrintedUrls,
    DateTimeOffset StartedAt);

/// <summary>Either the page that answered, or why there isn't one.</summary>
public sealed record AppReadiness(string? Url, string? Problem);

/// <summary>
/// Runs commands for sessions and keeps them alive past the tool call that started them: dev servers never
/// exit, so an agent's shell can't own them. Fleet finds the page by itself, whatever the framework: the
/// addresses the process prints and the ports its process tree listens on.
/// </summary>
public interface IAppRunner
{
    AppRunSnapshot Start(string sessionId, string directory, string command);

    AppRunSnapshot? Find(string appId);

    /// <summary>The newest run of <paramref name="command"/> in this session that hasn't exited.</summary>
    AppRunSnapshot? FindActive(string sessionId, string command);

    /// <summary>Waits until a page answers, the process exits, or <paramref name="timeout"/> passes.</summary>
    Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>The last <paramref name="maxLines"/> lines of output, stdout and stderr together, without colour codes.</summary>
    IReadOnlyList<string> Logs(string appId, int maxLines);

    Task<bool> StopAsync(string appId);

    /// <summary>Stops the run and starts the same command again under the same id, so canvases that show it keep working.</summary>
    Task<AppRunSnapshot?> RestartAsync(string appId);
}
