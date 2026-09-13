using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Browser;

/// <summary>An app that was started (or already ran) and the browser canvas that shows it.</summary>
public sealed record AppPreview(AppStartResult Started, Canvas Canvas);

/// <summary>
/// Shows pages in browser canvases, for the agent's tools and the user's + menu alike. A canvas's title is its
/// identity (<see cref="ICanvasService.OpenAsync"/>): opening a title again changes that canvas.
/// </summary>
public sealed class BrowserPreviews(ICanvasService canvases, AppRunService apps)
{
    public const string DefaultTitle = "Browser";

    /// <summary>
    /// Starts <paramref name="command"/> in the session's folder and shows it in the canvas titled
    /// <paramref name="title"/> at once; the page follows when it answers. The agent restarts a live run of the
    /// same command; the user (<paramref name="restartLive"/> false) gets the canvas that already shows it.
    /// </summary>
    public async Task<CanvasResult<AppPreview>> StartAppAsync(
        string sessionId,
        string command,
        string title,
        bool restartLive,
        CancellationToken ct = default)
    {
        var started = await apps.StartAsync(sessionId, command, restartLive);
        if (started.App is not { } app)
            return CanvasResult.Fail<AppPreview>(CanvasErrorKind.Invalid, started.Problem ?? "The app couldn't be started.");

        var tabs = await BrowserTabsAsync(sessionId, ct);
        if (!restartLive && tabs.FirstOrDefault(tab => tab.State.AppId == app.Id) is { } showing)
        {
            var focused = await canvases.FocusAsync(sessionId, showing.Canvas.Id, ct);
            return focused.IsSuccess
                ? CanvasResult.Ok(new AppPreview(started, focused.Value))
                : CanvasResult.Fail<AppPreview>(focused.Error);
        }

        // Keep the page the tab already shows for this app, so a restart doesn't blank it.
        var shown = tabs.FirstOrDefault(tab => tab.Canvas.Title == title && tab.State.AppId == app.Id)?.State.Url;
        var url = shown is { Length: > 0 } ? shown : app.Status == AppRunStatus.Running ? app.Url ?? string.Empty : string.Empty;

        var opened = await OpenPageAsync(sessionId, title, url, app.Id, ct);
        return opened.IsSuccess
            ? CanvasResult.Ok(new AppPreview(started, opened.Value.Canvas))
            : CanvasResult.Fail<AppPreview>(opened.Error);
    }

    /// <summary>Shows <paramref name="url"/> in the canvas titled <paramref name="title"/>, created or reopened as needed.</summary>
    public Task<CanvasResult<CanvasOutcome>> OpenPageAsync(string sessionId, string title, string url, string? appId, CancellationToken ct = default)
    {
        var state = new JsonObject { ["url"] = url };
        if (appId is not null)
            state["appId"] = appId;
        return canvases.OpenAsync(sessionId, CanvasKinds.Browser, title, state, ct);
    }

    /// <summary><paramref name="title"/> when there is one, else <paramref name="fallback"/>, cut to fit a tab.</summary>
    public static string TitleOr(string? title, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(title) ? fallback.Trim() : title.Trim();
        if (name.Length == 0)
            name = DefaultTitle;
        return name.Length <= CanvasLimits.MaxTitleLength ? name : name[..(CanvasLimits.MaxTitleLength - 1)] + "…";
    }

    private async Task<IReadOnlyList<BrowserTab>> BrowserTabsAsync(string sessionId, CancellationToken ct)
        => [.. (await canvases.ListAsync(sessionId, ct))
            .Select(item => item.Canvas)
            .Where(canvas => canvas.Kind == CanvasKinds.Browser)
            .Select(canvas => new BrowserTab(canvas, BrowserState.Parse(canvas.StateJson)))];

    private sealed record BrowserTab(Canvas Canvas, BrowserState State);
}
