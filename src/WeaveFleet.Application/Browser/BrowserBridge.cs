using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// The agent's browser tools (<c>fleet_app_start</c>, <c>fleet_browser_open</c>) for calls from a harness
/// process. Resolves the caller the same way as <see cref="CanvasBridge"/>, and shows pages as browser canvases.
/// </summary>
public sealed class BrowserBridge(
    IHarnessCanvasCallerResolver callers,
    IBackgroundUserScope userScope,
    ICanvasService canvases,
    ISessionRepository sessions,
    IAppRunner apps)
{
    /// <summary>Long enough for a first <c>dotnet run</c> or <c>npm install</c>-then-serve on a cold machine.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private const int FailureLogLines = 30;

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

            var session = await sessions.GetByIdAsync(sessionId);
            if (session is null || string.IsNullOrWhiteSpace(session.Directory) || !System.IO.Directory.Exists(session.Directory))
                return Invalid("This session has no folder on this machine to run the command in.");

            var active = apps.FindActive(sessionId, command);
            var restarted = active is not null;
            var app = active is null ? apps.Start(sessionId, session.Directory, command) : await apps.RestartAsync(active.Id);
            if (app is null)
                return Invalid("The app stopped while it was being restarted. Call fleet_app_start again.");

            var ready = await apps.WaitUntilReadyAsync(app.Id, ReadyTimeout, ct);
            if (ready.Url is null)
                return Invalid(FailureText(app.Id, ready.Problem ?? "No page answered."));

            var opened = await OpenPageAsync(sessionId, title, ready.Url, app.Id, ct);
            if (!opened.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);

            var canvas = opened.Value.Canvas;
            var current = apps.Find(app.Id) ?? app;
            var output = new StringBuilder()
                .Append(restarted ? "Restarted " : "Running ").Append('`').Append(command).Append("` (").Append(app.Id).Append(") in ").Append(session.Directory).Append('.')
                .Append("\nShowing ").Append(ready.Url).Append(" in ").Append(CanvasText.CanvasName(canvas)).Append('.');
            var otherPorts = current.Ports.Where(port => !ready.Url.Contains($":{port}", StringComparison.Ordinal)).ToList();
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
        return await canvases.OpenAsync(sessionId, CanvasKinds.Browser, string.IsNullOrWhiteSpace(title) ? "Browser" : title, state, ct);
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

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
