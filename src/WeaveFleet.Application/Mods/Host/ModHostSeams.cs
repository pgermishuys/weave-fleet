using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Whether a user's mods may run: the Mods switch and "Start without mods", read outside a request.</summary>
public interface IModUserGate
{
    /// <summary>The user's Mods switch (Settings → Experimental), or the option when they haven't chosen.</summary>
    Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct);

    /// <summary>"Start without mods" is set for the user.</summary>
    bool IsSafeMode(string userId);
}

/// <summary>Records three strikes in the store, as the user's own <see cref="ModService"/> would, raising <c>mods.changed</c>.</summary>
public interface IModStrikeRecorder
{
    /// <summary>A kept mod: turned off everywhere with <paramref name="message"/>.</summary>
    Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct);

    /// <summary>A draft: turned off in its session with <paramref name="message"/>.</summary>
    Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct);
}

/// <summary>
/// The <c>$.ui</c> calls that reach the browser, and where a session is open. M6 implements it; until then
/// <see cref="LoggingModHostUi"/> logs them.
/// </summary>
public interface IModHostUi
{
    Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct);

    Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct);

    /// <param name="Tone"><c>accent</c>, <c>warn</c> or null.</param>
    Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct);

    /// <summary>Where the session is open now: <c>desktop</c>, <c>phone</c>, or none.</summary>
    IReadOnlyList<string> SurfacesOf(string userId, string sessionId);
}

/// <summary>What the host tells Fleet that M6 redraws from. Until M6, nothing listens.</summary>
public interface IModHostSignals
{
    /// <summary>A mod asked for its sites to be drawn again: in one session, or in every session when <paramref name="sessionId"/> is null.</summary>
    void Invalidated(string userId, string modId, string? sessionId);

    /// <summary>The host was restarted and every mod reloaded: what Fleet had drawn from mods is stale.</summary>
    void Restarted(string userId);
}

/// <summary>The Bun the host runs on, without installing one (installing is the Mods switch's job, M2b).</summary>
public interface IModHostBun
{
    /// <summary>The Bun to run now, or null when there's none yet.</summary>
    Task<BunLocation?> FindAsync(CancellationToken ct);

    /// <summary>Deletes Fleet's older Bun versions, keeping those in <paramref name="inUse"/>. Called once the host has moved to a newer one.</summary>
    Task PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct);
}

/// <summary>Where the host's script is.</summary>
public interface IModHostFiles
{
    /// <summary><c>{app}/mods-host/host.js</c>, or <c>mods/host/dist/host.js</c> when running from the repository; null when neither exists.</summary>
    string? HostScript { get; }

    /// <summary>Fleet's version, for <c>initialize</c>.</summary>
    string FleetVersion { get; }
}

/// <summary>Watches a user's drafts for saves.</summary>
public interface IModDraftWatcher
{
    /// <summary>
    /// Watches <paramref name="draftsRoot"/> (<c>drafts/</c>, holding <c>{sessionId}/{name}/</c>) and calls
    /// <paramref name="changed"/> once a draft has been quiet for the debounce after a change: a file written, added,
    /// renamed or deleted, or the draft's folder created or deleted. Never follows links. Watches only what exists and
    /// picks up folders created later. Dispose to stop.
    /// </summary>
    IDisposable Watch(string draftsRoot, Action<ModDraftChange> changed);
}

public sealed record ModDraftChange(string SessionId, string Name);

/// <summary>Logs <c>$.ui</c> calls until M6 draws them.</summary>
public sealed partial class LoggingModHostUi(ILogger<LoggingModHostUi> logger) : IModHostUi
{
    public Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct)
    {
        LogPane(logger, modId, "opened", paneId, sessionId);
        return Task.CompletedTask;
    }

    public Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct)
    {
        LogPane(logger, modId, "closed", paneId, sessionId);
        return Task.CompletedTask;
    }

    public Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct)
    {
        LogToast(logger, modId, sessionId, text);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> SurfacesOf(string userId, string sessionId) => [];

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod {ModId} {Action} pane {PaneId} in session {SessionId}")]
    private static partial void LogPane(ILogger logger, string modId, string action, string paneId, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod {ModId} toast in session {SessionId}: {Text}")]
    private static partial void LogToast(ILogger logger, string modId, string sessionId, string text);
}

/// <summary>Nothing listens to the host's signals until M6.</summary>
public sealed class NoModHostSignals : IModHostSignals
{
    public void Invalidated(string userId, string modId, string? sessionId)
    {
    }

    public void Restarted(string userId)
    {
    }
}
