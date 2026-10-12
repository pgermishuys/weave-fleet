using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>One event for one session.</summary>
/// <param name="Event"><c>session.start</c>, <c>turn.complete</c>, <c>ui.render</c>, <c>ui.press</c>, <c>ui.input</c> or <c>ui.select</c>.</param>
/// <param name="E">The event as mods see it, before any hook. Control events carry the control's <c>handle</c>.</param>
/// <param name="Surface"><c>desktop</c> or <c>phone</c>, for control events.</param>
public sealed record ModDispatchRequest(string Event, string SessionId, JsonElement E, string? Surface = null, string? Mod = null);

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
/// What mods ask of the browser: panes, toasts, redraws, log lines, and where a session is open. Redraws are real
/// (<see cref="BroadcastingModHostUi"/>), and so are panes; the rest answers and drops (<see cref="NoModHostUi"/>) until toasts land.
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

    /// <summary>The user's loaded mods changed (loaded, reloaded or dropped): every session asks again which sites they hook.</summary>
    void Loaded(string userId);

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

    public void Loaded(string userId)
    {
    }

    public void Logged(string userId, string modId, string? sessionId, string level, string text)
    {
    }
}

/// <summary>
/// <see cref="NoModHostUi"/> except that a redraw reaches the browser as <c>mod.ui</c> (on the session's topic when a
/// session is named, else on the user's <c>sessions</c> topic) and a pane open or close as <c>mod.pane</c>.
/// </summary>
public sealed class BroadcastingModHostUi(IEventBroadcaster events) : IModHostUi
{
    private readonly NoModHostUi _none = new();

    public Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct)
        => PaneAsync(userId, modId, sessionId, paneId, title, true);

    public Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct)
        => PaneAsync(userId, modId, sessionId, paneId, null, false);

    /// <summary>Best effort, like a redraw: a pane event that can't be sent is dropped.</summary>
    private async Task PaneAsync(string userId, string modId, string sessionId, string paneId, string? title, bool open)
    {
        var at = modId.IndexOf('@', StringComparison.Ordinal);
        var payload = new ModPanePayload { SessionId = sessionId, Mod = at > 0 ? modId[..at] : modId, Id = paneId, Title = title, Open = open };
        try
        {
            await events.BroadcastAsync(
                $"session:{sessionId}",
                EventTypes.ModPane,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.ModPanePayload),
                new ModPane { Payload = payload },
                userId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to tell the mod.
        }
    }

    public Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct)
        => _none.ToastAsync(userId, modId, sessionId, text, timeoutMs, tone, ct);

    public IReadOnlyList<string> SurfacesOf(string userId, string sessionId) => _none.SurfacesOf(userId, sessionId);

    public void Invalidated(string userId, string modId, string? sessionId)
    {
        var at = modId.IndexOf('@', StringComparison.Ordinal);
        var payload = new ModUiPayload { SessionId = sessionId, Mod = at > 0 ? modId[..at] : modId };
        _ = SendAsync(userId, sessionId is null ? "sessions" : $"session:{sessionId}", payload);
    }

    /// <summary>Fleet's own redraw, with no mod: on the <c>sessions</c> topic, so every session asks for its sites again.</summary>
    public void Loaded(string userId) => _ = SendAsync(userId, "sessions", new ModUiPayload());

    public void Logged(string userId, string modId, string? sessionId, string level, string text)
        => _none.Logged(userId, modId, sessionId, level, text);

    /// <summary>A redraw is best effort: one that can't be sent is dropped, and the mod asks again.</summary>
    private async Task SendAsync(string userId, string topic, ModUiPayload payload)
    {
        try
        {
            await events.BroadcastAsync(
                topic,
                EventTypes.ModUi,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.ModUiPayload),
                new ModUi { Payload = payload },
                userId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to tell the mod, and nobody awaits this.
        }
    }
}
