using System.Text;
using System.Text.Json.Nodes;
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
    ICanvasService canvases,
    AppRunService apps)
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

            var started = await apps.StartAsync(sessionId, command);
            if (started.App is not { } app)
                return Invalid(started.Problem ?? "The app couldn't be started.");

            var shown = await OpenPageAsync(sessionId, title, await ShownUrlAsync(sessionId, title, app.Id, ct), app.Id, ct);
            if (!shown.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(shown.Error);

            var ready = await apps.WaitUntilReadyAsync(app.Id, ReadyTimeout, ct);
            if (ready.Url is null)
                return Invalid(FailureText(app.Id, ready.Problem ?? "No page answered."));

            var opened = await OpenPageAsync(sessionId, title, ready.Url, app.Id, ct);
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

            var opened = await OpenPageAsync(sessionId, title, page.ToString(), appId: null, ct);
            if (!opened.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);

            var canvas = opened.Value.Canvas;
            return CanvasResult.Ok(new CanvasToolOutput(
                $"{canvas.Title} · {page}",
                $"Showing {page} in {CanvasText.CanvasName(canvas)}.",
                canvas.Id,
                canvas.Version));
        }, ct);

    /// <summary>Status and recent output of the app a browser canvas shows, for <c>fleet_canvas_read</c>.</summary>
    public static string RenderApp(AppRunSnapshot app, IReadOnlyList<string> logs)
    {
        var text = new StringBuilder()
            .Append("app ").Append(app.Id).Append(' ').Append(app.Status.ToString().ToLowerInvariant());
        if (app.ExitCode is { } exitCode)
            text.Append(" (exit code ").Append(exitCode).Append(')');
        text.Append("\ncommand ").Append(app.Command);
        if (app.Ports.Count > 0)
            text.Append("\nports ").AppendJoin(", ", app.Ports);
        if (logs.Count > 0)
            text.Append("\nlast output:\n").AppendJoin('\n', logs);
        return text.ToString();
    }

    private async Task<CanvasResult<CanvasOutcome>> OpenPageAsync(string sessionId, string? title, string url, string? appId, CancellationToken ct)
    {
        var state = new JsonObject { ["url"] = url };
        if (appId is not null)
            state["appId"] = appId;
        return await canvases.OpenAsync(sessionId, CanvasKinds.Browser, TitleOrDefault(title), state, ct);
    }

    /// <summary>The page the tab already shows for this app, so a restart doesn't blank it; empty ("starting") otherwise.</summary>
    private async Task<string> ShownUrlAsync(string sessionId, string? title, string appId, CancellationToken ct)
    {
        var name = TitleOrDefault(title);
        var tab = (await canvases.ListAsync(sessionId, ct))
            .Select(item => item.Canvas)
            .FirstOrDefault(canvas => canvas.Kind == CanvasKinds.Browser && canvas.Title == name);
        if (tab is null)
            return string.Empty;

        var state = BrowserState.Parse(tab.StateJson);
        return state.AppId == appId ? state.Url : string.Empty;
    }

    private static string TitleOrDefault(string? title) => string.IsNullOrWhiteSpace(title) ? "Browser" : title.Trim();

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

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
