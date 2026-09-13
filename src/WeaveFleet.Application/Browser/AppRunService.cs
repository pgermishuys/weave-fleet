using System.Globalization;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// A session's apps as the user and the agent see them: live runs from <see cref="IAppRunner"/>, and stored
/// ones that aren't running (after Fleet restarted, say), which can be started again under the same id and
/// port. Every call is for the current user's session; an app of another session or user isn't found.
/// </summary>
public sealed class AppRunService(
    IAppRunner apps,
    IAppRunRepository runs,
    ISessionRepository sessions,
    IUserContext userContext)
{
    /// <summary>
    /// Runs <paramref name="command"/> in the session's folder. When the session already has a run of that
    /// command, live or stored, it starts that one again, so the canvas showing it keeps working.
    /// </summary>
    public async Task<AppStartResult> StartAsync(string sessionId, string command)
    {
        var session = await sessions.GetByIdAsync(sessionId);
        if (session is null)
            return AppStartResult.Fail($"Session {sessionId} not found.");
        if (IsArchived(session))
            return AppStartResult.Fail(ArchivedProblem);
        if (string.IsNullOrWhiteSpace(session.Directory) || !Directory.Exists(session.Directory))
            return AppStartResult.Fail("This session has no folder on this machine to run the command in.");

        if (apps.FindActive(sessionId, command) is { } active)
            return FromOutcome(await apps.RestartAsync(active.Id), restarted: true);

        var stored = (await runs.ListBySessionIdAsync(sessionId)).LastOrDefault(run => run.Command == command);
        var request = stored is null
            ? new AppRunRequest("app_" + Ulid.NewUlid(), sessionId, userContext.UserId, session.Directory, command)
            : new AppRunRequest(stored.Id, sessionId, userContext.UserId, session.Directory, command, stored.Port);
        return FromOutcome(await apps.StartAsync(request), restarted: false);
    }

    /// <summary>Starts the app again: restarts it when it's live, or starts a stopped or exited one.</summary>
    public async Task<AppStartResult> RestartAsync(string sessionId, string appId)
    {
        var session = await sessions.GetByIdAsync(sessionId);
        if (session is null)
            return AppStartResult.NotFound(appId);
        if (IsArchived(session))
            return AppStartResult.Fail(ArchivedProblem);

        if (apps.Find(appId) is { } live)
        {
            return Owns(live, sessionId)
                ? FromOutcome(await apps.RestartAsync(appId), restarted: live.IsLive)
                : AppStartResult.NotFound(appId);
        }

        var stored = await runs.GetByIdAsync(sessionId, appId);
        if (stored is null)
            return AppStartResult.NotFound(appId);
        if (!Directory.Exists(stored.Directory))
            return AppStartResult.Fail($"The app's folder {stored.Directory} doesn't exist any more.");

        var request = new AppRunRequest(stored.Id, sessionId, userContext.UserId, stored.Directory, stored.Command, stored.Port);
        return FromOutcome(await apps.StartAsync(request), restarted: false);
    }

    public async Task<bool> StopAsync(string sessionId, string appId)
    {
        if (apps.Find(appId) is { } live)
            return Owns(live, sessionId) && await apps.StopAsync(appId);

        // Stored but not running: nothing to stop.
        return await runs.GetByIdAsync(sessionId, appId) is not null;
    }

    /// <summary>The app as it is now; a stored run Fleet isn't running comes back stopped (or exited, if it had).</summary>
    public async Task<AppRunSnapshot?> GetAsync(string sessionId, string appId)
    {
        if (apps.Find(appId) is { } live)
            return Owns(live, sessionId) ? live : null;

        var stored = await runs.GetByIdAsync(sessionId, appId);
        return stored is null ? null : FromStored(stored);
    }

    /// <summary>Output after the first <paramref name="after"/> lines. A stored run's output went with the Fleet that ran it.</summary>
    public async Task<AppOutput?> OutputAsync(string sessionId, string appId, long after)
    {
        if (apps.Find(appId) is { } live)
            return Owns(live, sessionId) ? apps.Output(appId, after) : null;

        return await runs.GetByIdAsync(sessionId, appId) is null ? null : new AppOutput([], 0);
    }

    public IReadOnlyList<string> Logs(string appId, int maxLines) => apps.Logs(appId, maxLines);

    public Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
        => apps.WaitUntilReadyAsync(appId, timeout, ct);

    private const string ArchivedProblem = "This session is archived, so Fleet doesn't run apps for it.";

    private static bool IsArchived(Session session) => string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal);

    private bool Owns(AppRunSnapshot app, string sessionId)
        => app.SessionId == sessionId && app.UserId == userContext.UserId;

    private static AppStartResult FromOutcome(AppStartOutcome outcome, bool restarted)
        => outcome.App is { } app ? new AppStartResult(app, restarted, null) : AppStartResult.Fail(outcome.Refusal ?? "The app couldn't be started.");

    internal static AppRunSnapshot FromStored(AppRun run)
    {
        var exited = run.Status == "exited";
        var updated = DateTimeOffset.TryParse(run.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;
        return new AppRunSnapshot(
            run.Id, run.SessionId, run.UserId, run.Command, run.Directory, run.Port,
            exited ? AppRunStatus.Exited : AppRunStatus.Stopped,
            exited ? run.ExitCode : null,
            Url: run.Url, Ports: [], HelperPorts: [], PrintedUrls: [], Pid: null, PidStartedAt: null, StartedAt: updated);
    }
}

/// <summary>The app that was started, whether it was a restart of a live run, or why nothing started.</summary>
public sealed record AppStartResult(AppRunSnapshot? App, bool Restarted, string? Problem)
{
    public bool IsNotFound { get; private init; }

    public static AppStartResult Fail(string problem) => new(null, false, problem);

    public static AppStartResult NotFound(string appId) => new(null, false, $"App {appId} not found.") { IsNotFound = true };
}
