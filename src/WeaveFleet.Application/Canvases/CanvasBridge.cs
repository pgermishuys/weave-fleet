using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>
/// What a canvas tool hands back to the agent: a title for the tool card, the text the model reads, and the
/// canvas it was about, if any.
/// </summary>
public sealed record CanvasToolOutput(string Title, string Output, string? CanvasId = null, int? Version = null);

/// <summary>
/// The agent's canvas tools (<c>fleet_canvas_*</c>) for calls that arrive from a harness process. Each call
/// finds its Fleet session through <see cref="IHarnessCanvasCallerResolver"/>, runs as the session's owner,
/// and returns short text for the model. Canvases are read-only for the user in this step (plan Decision 7),
/// so reads are always full and nothing mentions user edits.
/// </summary>
public sealed class CanvasBridge(
    IHarnessCanvasCallerResolver callers,
    IBackgroundUserScope userScope,
    ICanvasService canvases,
    AppRunService apps)
{
    /// <summary>The one answer for every call Fleet can't place: unknown token, unknown session, or another process's session.</summary>
    public const string UnknownCallerMessage = "Fleet couldn't match this call to one of its sessions.";

    private const int BrowserLogLines = 40;

    public Task<CanvasResult<CanvasToolOutput>> ListAsync(string? bridgeToken, string? harnessSessionId, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            var items = await canvases.ListAsync(sessionId, ct);
            var output = items.Count == 0
                ? "No open canvases."
                : string.Join('\n', items.Select(item => CanvasText.RenderListLine(item.Canvas, changedByUser: false)));
            return CanvasResult.Ok(new CanvasToolOutput(items.Count == 1 ? "1 canvas" : $"{items.Count} canvases", output));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> OpenAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? kind,
        string? title,
        JsonNode? state,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            var opened = await canvases.OpenAsync(sessionId, kind ?? string.Empty, title ?? string.Empty, state, ct);
            if (!opened.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);

            var (canvas, summary, created) = opened.Value;
            var output = created
                ? $"Opened {CanvasText.CanvasName(canvas)} at v{canvas.Version}."
                : $"Opened {CanvasText.CanvasName(canvas)} at v{canvas.Version} ({summary}).";
            return Ok(canvas, summary, output);
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> ReadAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? canvasId,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (string.IsNullOrWhiteSpace(canvasId))
                return MissingCanvasId();

            var read = await canvases.ReadAsync(sessionId, canvasId, full: true, ct);
            if (!read.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(read.Error);

            var canvas = await canvases.GetAsync(sessionId, canvasId, ct);
            var text = read.Value;
            if (canvas?.Kind == CanvasKinds.Browser
                && BrowserState.Parse(canvas.StateJson).AppId is { } appId
                && await apps.GetAsync(sessionId, appId) is { } app)
            {
                text += "\n" + BrowserBridge.RenderApp(app, apps.Logs(appId, BrowserLogLines));
            }

            return CanvasResult.Ok(canvas is null
                ? new CanvasToolOutput(canvasId, text, canvasId)
                : new CanvasToolOutput($"{canvas.Title} · v{canvas.Version}", text, canvas.Id, canvas.Version));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> PatchAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? canvasId,
        JsonNode? ops,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (string.IsNullOrWhiteSpace(canvasId))
                return MissingCanvasId();

            var applied = await canvases.ApplyAsync(sessionId, canvasId, CanvasActor.Agent, ops, ct);
            if (!applied.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(applied.Error);

            var (canvas, summary, _) = applied.Value;
            return Ok(canvas, summary, $"Updated to v{canvas.Version} ({summary}).");
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> FocusAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? canvasId,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (string.IsNullOrWhiteSpace(canvasId))
                return MissingCanvasId();

            var focused = await canvases.FocusAsync(sessionId, canvasId, ct);
            if (!focused.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(focused.Error);

            var canvas = focused.Value;
            return CanvasResult.Ok(new CanvasToolOutput(
                $"{canvas.Title} · v{canvas.Version}",
                $"Showing {CanvasText.CanvasName(canvas)} at v{canvas.Version}.",
                canvas.Id,
                canvas.Version));
        }, ct);

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

    private static CanvasResult<CanvasToolOutput> Ok(Canvas canvas, string summary, string output)
        => CanvasResult.Ok(new CanvasToolOutput($"{canvas.Title} · {summary} · v{canvas.Version}", output, canvas.Id, canvas.Version));

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, UnknownCallerMessage);

    private static CanvasResult<CanvasToolOutput> MissingCanvasId()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, "\"canvasId\" is required. Call fleet_canvas_list to see the open canvases.");
}
