using System.Text.Json;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>One event for one session.</summary>
/// <param name="Event"><c>session.start</c>, <c>turn.complete</c>, <c>ui.render</c>, <c>ui.press</c>, <c>ui.input</c> or <c>ui.select</c>.</param>
/// <param name="E">The event as mods see it, before any hook. Control events carry the control's <c>handle</c>.</param>
/// <param name="Surface"><c>desktop</c> or <c>phone</c>, for control events.</param>
public sealed record ModDispatchRequest(string Event, string SessionId, JsonElement E, string? Surface = null);

/// <param name="Dispatched">False when nothing crossed the pipe or the host didn't answer: draw Fleet's own.</param>
/// <param name="Result">The chain's result: for <c>ui.render</c> a <c>WireElement</c>, <c>null</c>, or <c>{ "type": "Fleet" }</c>.</param>
/// <param name="DrawnBy">Which mods' trees are in a <c>ui.render</c> result, for Draft marking.</param>
public sealed record ModDispatchResult(bool Dispatched, JsonElement? Result, IReadOnlyList<string> DrawnBy)
{
    public static ModDispatchResult NotDispatched { get; } = new(false, null, []);
}

/// <summary>A load the host refused (error -32001, or the store couldn't stage the draft): why, with the check report.</summary>
public sealed record ModLoadProblem(string Message, JsonElement? Report);

/// <summary>Turns a mod off after the host counts three strikes, through the user's own <see cref="ModService"/>.</summary>
public interface IModStrikeRecorder
{
    Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct);

    Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct);
}

/// <summary>
/// What mods ask of the browser: panes, toasts, redraws, log lines, and where a session is open. M6 implements it; until
/// then <see cref="NoModHostUi"/> answers and drops.
/// </summary>
public interface IModHostUi
{
    Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct);

    Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct);

    Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct);

    /// <summary>Where the session is open now: <c>desktop</c>, <c>phone</c>, or none.</summary>
    IReadOnlyList<string> SurfacesOf(string userId, string sessionId);

    /// <summary>A mod asked for its sites to be drawn again: in one session, or every session when <paramref name="sessionId"/> is null.</summary>
    void Invalidated(string userId, string modId, string? sessionId);

    void Logged(string userId, string modId, string? sessionId, string level, string text);
}

/// <summary>Until M6 draws for mods: every <c>$.ui</c> call succeeds and goes nowhere.</summary>
public sealed class NoModHostUi : IModHostUi
{
    public Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct) => Task.CompletedTask;

    public Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct) => Task.CompletedTask;

    public Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<string> SurfacesOf(string userId, string sessionId) => [];

    public void Invalidated(string userId, string modId, string? sessionId)
    {
    }

    public void Logged(string userId, string modId, string? sessionId, string level, string text)
    {
    }
}
