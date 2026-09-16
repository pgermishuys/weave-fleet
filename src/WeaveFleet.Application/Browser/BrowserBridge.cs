using System.Text;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// The agent's browser tools (<c>fleet_app_start</c>, <c>fleet_browser_open</c>) for calls from a harness
/// process. Resolves the caller the same way as <see cref="CanvasBridge"/>, and shows pages as browser canvases.
/// </summary>
public sealed class BrowserBridge(
    IHarnessCanvasCallerResolver callers,
    IBackgroundUserScope userScope,
    BrowserPreviews previews,
    AppRunService apps,
    ICanvasService canvases,
    IScreenshotter screenshots)
{
    /// <summary>Long enough for a first <c>dotnet run</c> or <c>npm install</c>-then-serve on a cold machine.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private const int FailureLogLines = 30;

    /// <summary>
    /// Starts the command and shows its tab at once, "starting"; the page follows when it answers. The call
    /// still waits for that, so it can tell the agent the address or why there isn't one.
    /// </summary>
    public Task<CanvasResult<CanvasToolOutput>> AppStartAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? command,
        string? title,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            command = command?.Trim();
            if (string.IsNullOrEmpty(command))
                return Invalid("\"command\" is required, e.g. \"npm run dev\" or \"dotnet watch\".");

            var name = BrowserPreviews.TitleOr(title, BrowserPreviews.DefaultTitle);
            var preview = await previews.StartAppAsync(sessionId, command, name, restartLive: true, ct);
            if (!preview.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(preview.Error);

            var (started, app) = (preview.Value.Started, preview.Value.Started.App!);
            var ready = await apps.WaitUntilReadyAsync(app.Id, ReadyTimeout, ct);
            if (ready.Url is null)
                return Invalid(FailureText(app.Id, ready.Problem ?? "No page answered."));

            var opened = await previews.OpenPageAsync(sessionId, name, ready.Url, app.Id, ct);
            if (!opened.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);

            var canvas = opened.Value.Canvas;
            var current = await apps.GetAsync(sessionId, app.Id) ?? app;
            var output = new StringBuilder()
                .Append(started.Restarted ? "Restarted " : "Running ").Append('`').Append(command).Append("` (").Append(app.Id).Append(") in ").Append(app.Directory).Append('.')
                .Append("\nShowing ").Append(ready.Url).Append(" in ").Append(CanvasText.CanvasName(canvas)).Append('.');
            var shownPort = new Uri(ready.Url).Port;
            var otherPorts = current.Ports.Where(port => port != shownPort).ToList();
            if (otherPorts.Count > 0)
                output.Append("\nIt also listens on ").AppendJoin(", ", otherPorts).Append('.');
            output.Append("\nCalling fleet_app_start again with the same command restarts it. fleet_canvas_read shows its status and recent output.");

            return CanvasResult.Ok(new CanvasToolOutput($"{canvas.Title} · {ready.Url}", output.ToString(), canvas.Id, canvas.Version));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> BrowserOpenAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? url,
        string? title,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (!LoopbackUrl.TryParse(url, out var page))
                return Invalid(LoopbackUrl.Requirement);

            var opened = await previews.OpenPageAsync(sessionId, BrowserPreviews.TitleOr(title, BrowserPreviews.DefaultTitle), page.ToString(), appId: null, ct);
            if (!opened.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);

            var canvas = opened.Value.Canvas;
            return CanvasResult.Ok(new CanvasToolOutput(
                $"{canvas.Title} · {page}",
                $"Showing {page} in {CanvasText.CanvasName(canvas)}.",
                canvas.Id,
                canvas.Version));
        }, ct);

    /// <summary>
    /// Takes a picture of the page a browser canvas shows and hands it back with the text, so the agent can
    /// look at the UI it just changed. <paramref name="path"/> moves to another page of the same app without
    /// opening a second canvas; the shot is taken off to the side, so what the user is looking at doesn't move.
    /// </summary>
    public Task<CanvasResult<CanvasToolOutput>> ScreenshotAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? canvasId,
        string? path,
        string? viewport,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (string.IsNullOrWhiteSpace(canvasId))
                return Invalid("\"canvasId\" is required: it's the browser canvas from fleet_app_start, fleet_browser_open or fleet_canvas_list.");
            if (!ScreenshotViewports.TryResolve(viewport, out var width, out var height))
                return Invalid(ScreenshotViewports.Requirement);

            var canvas = await canvases.GetAsync(sessionId, canvasId, ct);
            if (canvas is null)
                return NotFound($"No canvas {canvasId} in this session.");
            if (canvas.Kind != CanvasKinds.Browser)
                return Invalid($"{CanvasText.CanvasName(canvas)} is a {canvas.Kind} canvas. Screenshots are of pages: use fleet_app_start or fleet_browser_open first.");

            var state = BrowserState.Parse(canvas.StateJson);
            var page = await PageAsync(sessionId, state, ct);
            if (page.Problem is { } why)
                return Invalid(why);

            if (!TryResolvePath(page.Url!, path, out var target, out var badPath))
                return Invalid(badPath);

            var shot = await screenshots.CaptureAsync(new ScreenshotRequest(target.ToString(), width, height), ct);
            if (shot.Image is not { } image)
                return Invalid(shot.Problem ?? "The screenshot failed.");

            var output = new StringBuilder()
                .Append("Screenshot of ").Append(target).Append(" at ").Append(width).Append('×').Append(height)
                .Append(", from ").Append(CanvasText.CanvasName(canvas)).Append('.')
                .Append("\nThe image is attached: look at it, don't guess. Call this again after a change to see it.");

            return CanvasResult.Ok(new CanvasToolOutput(
                $"{canvas.Title} · {width}×{height}",
                output.ToString(),
                canvas.Id,
                canvas.Version,
                [new CanvasToolAttachment("image/png", "screenshot.png", image.Png)]));
        }, ct);

    /// <summary>The page the canvas shows, or why there isn't one to shoot yet.</summary>
    private async Task<(string? Url, string? Problem)> PageAsync(string sessionId, BrowserState state, CancellationToken ct)
    {
        var app = state.AppId is { } appId ? await apps.GetAsync(sessionId, appId) : null;
        if (app is not null && app.Status != AppRunStatus.Running)
        {
            var problem = new StringBuilder("The app in this canvas isn't running (")
                .Append(app.Status.ToString().ToLowerInvariant())
                .Append("), so there's no page to shoot. Start it again with fleet_app_start.");
            var logs = apps.Logs(app.Id, FailureLogLines);
            if (logs.Count > 0)
                problem.Append("\nLast output:\n").AppendJoin('\n', logs);
            return (null, problem.ToString());
        }

        var url = state.Url is { Length: > 0 } shown ? shown : app?.Url;
        return string.IsNullOrEmpty(url)
            ? (null, "This canvas has no page yet. Wait for the app to serve one, or call fleet_app_start again.")
            : (url, null);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against the page the canvas shows. Empty means that page. Anything that
    /// lands on another machine is refused: Fleet's browser only ever opens pages running here.
    /// </summary>
    private static bool TryResolvePath(string page, string? path, out Uri target, out string problem)
    {
        problem = string.Empty;
        target = new Uri(page);
        if (string.IsNullOrWhiteSpace(path))
            return true;

        if (!Uri.TryCreate(target, path.Trim(), out var resolved))
        {
            problem = $"\"{path}\" isn't a path Fleet can resolve against {page}. Use \"\" for the page in the canvas, or a path like \"/settings\".";
            return false;
        }

        if (!LoopbackUrl.TryParse(resolved.ToString(), out target))
        {
            problem = LoopbackUrl.Requirement;
            return false;
        }

        return true;
    }

    /// <summary>Status and recent output of the app a browser canvas shows, for <c>fleet_canvas_read</c>.</summary>
    public static string RenderApp(AppRunSnapshot app, IReadOnlyList<string> logs)
    {
        var text = new StringBuilder()
            .Append("app ").Append(app.Id).Append(' ').Append(app.Status.ToString().ToLowerInvariant());
        if (app.ExitCode is { } exitCode)
            text.Append(" (exit code ").Append(exitCode).Append(')');
        text.Append("\ncommand ").Append(app.Command);
        // A tab the user started from the + menu has no url of its own: the page is the app's.
        if (app.Status == AppRunStatus.Running && app.Url is not null)
            text.Append("\npage ").Append(app.Url);
        if (app.Ports.Count > 0)
            text.Append("\nports ").AppendJoin(", ", app.Ports);
        if (logs.Count > 0)
            text.Append("\nlast output:\n").AppendJoin('\n', logs);
        return text.ToString();
    }

    private string FailureText(string appId, string problem)
    {
        var logs = apps.Logs(appId, FailureLogLines);
        var text = new StringBuilder(problem);
        if (logs.Count > 0)
            text.Append("\nLast output:\n").AppendJoin('\n', logs);
        text.Append("\nIf it serves on a port Fleet can't see (a container, say), call fleet_browser_open with its address.");
        return text.ToString();
    }

    private async Task<CanvasResult<CanvasToolOutput>> RunAsync(
        string? bridgeToken,
        string? harnessSessionId,
        Func<string, Task<CanvasResult<CanvasToolOutput>>> call,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct);
        if (caller is null)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
            return await call(caller.FleetSessionId);
    }

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);

    private static CanvasResult<CanvasToolOutput> NotFound(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, message);

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
