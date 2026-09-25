namespace WeaveFleet.Application.Browser;

public enum AppRunStatus
{
    /// <summary>The process is up, but nothing on it answers HTTP yet.</summary>
    Starting,

    /// <summary>A page answers.</summary>
    Running,

    /// <summary>The process ended on its own; <see cref="AppRunSnapshot.ExitCode"/> says how.</summary>
    Exited,

    /// <summary>Fleet stopped it: the user or a session lifecycle asked, or Fleet restarted.</summary>
    Stopped,
}

/// <summary>What just happened to a run, for <see cref="IAppRunner.Changed"/> and the <c>app.updated</c> event.</summary>
public enum AppChangeReason
{
    Started,
    Ready,
    Restarted,
    Stopped,
    Exited,
}

/// <summary>
/// A command Fleet runs for a session, usually a dev server. <see cref="Url"/> is the page Fleet found,
/// <see cref="Ports"/> the TCP ports the app's process tree listens on, <see cref="HelperPorts"/> those of its
/// dev tools that serve no page (<c>dotnet watch</c>'s refresh servers), and <see cref="PrintedUrls"/> the local
/// addresses it printed, in order. <see cref="Port"/> is the <c>PORT</c> it gets, the same on every start.
/// </summary>
public sealed record AppRunSnapshot(
    string Id,
    string SessionId,
    string UserId,
    string Command,
    string Directory,
    int Port,
    AppRunStatus Status,
    int? ExitCode,
    string? Url,
    IReadOnlyList<int> Ports,
    IReadOnlyList<int> HelperPorts,
    IReadOnlyList<string> PrintedUrls,
    int? Pid,
    DateTimeOffset? PidStartedAt,
    DateTimeOffset StartedAt)
{
    /// <summary>Starting or running: it has a process, and counts towards the caps.</summary>
    public bool IsLive => Status is AppRunStatus.Starting or AppRunStatus.Running;
}

/// <summary>
/// Start a run. <paramref name="Id"/> and <paramref name="Port"/> come from the stored run when it's started
/// again after Fleet restarted, so canvases that show it keep working; a new run gets a new id and no port.
/// </summary>
public sealed record AppRunRequest(string Id, string SessionId, string UserId, string Directory, string Command, int? Port = null);

/// <summary>Either the run, or why Fleet wouldn't start it (a cap).</summary>
public sealed record AppStartOutcome(AppRunSnapshot? App, string? Refusal);

/// <summary>Either the page that answered, or why there isn't one.</summary>
public sealed record AppReadiness(string? Url, string? Problem);

/// <summary>Output lines from line <c>after</c> on, and the number to ask with next time.</summary>
public sealed record AppOutput(IReadOnlyList<string> Lines, long Next);

public sealed record AppRunChange(AppRunSnapshot App, AppChangeReason Reason);

/// <summary>Stops a session's apps when the session is archived or deleted.</summary>
public interface ISessionAppCleanup
{
    Task StopSessionAppsAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Runs commands for sessions and keeps them alive past the tool call that started them: dev servers never
/// exit, so an agent's shell can't own them. Fleet finds the page by itself, whatever the framework: the
/// addresses the process prints and the ports its process tree listens on. Runs live in memory; callers
/// store them from <see cref="Changed"/>. Nothing here checks who's asking: callers check the session first.
/// </summary>
public interface IAppRunner
{
    /// <summary>Raised after every change, on whatever thread made it. Handlers must return quickly.</summary>
    event Action<AppRunChange>? Changed;

    /// <summary>
    /// Starts the run, or starts it again when a run with that id is already known (same id, same port).
    /// Refuses when the session or Fleet is at its cap of live apps.
    /// </summary>
    Task<AppStartOutcome> StartAsync(AppRunRequest request);

    AppRunSnapshot? Find(string appId);

    /// <summary>Whether a live run listens on <paramref name="port"/> (its app's ports, not its dev tools').</summary>
    bool IsAppPort(int port);

    /// <summary>The newest live run of <paramref name="command"/> in this session.</summary>
    AppRunSnapshot? FindActive(string sessionId, string command);

    /// <summary>Waits until a page answers, the process ends, or <paramref name="timeout"/> passes.</summary>
    Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>The last <paramref name="maxLines"/> lines of output, stdout and stderr together, without colour codes.</summary>
    IReadOnlyList<string> Logs(string appId, int maxLines);

    /// <summary>Output after the first <paramref name="after"/> lines, as far back as Fleet still has it.</summary>
    AppOutput Output(string appId, long after);

    /// <summary>Stops the run and its whole process tree. The run is kept, stopped, so it can be started again.</summary>
    Task<bool> StopAsync(string appId);

    /// <summary>Stops the run and starts the same command again under the same id, so canvases that show it keep working.</summary>
    Task<AppStartOutcome> RestartAsync(string appId);

    /// <summary>
    /// Kills a process tree a previous Fleet left running, if <paramref name="pid"/> is still the process that
    /// started at <paramref name="startedAt"/> (and not a later one that reuses the id). Returns whether it did.
    /// </summary>
    bool KillLeftover(int pid, DateTimeOffset startedAt);
}
