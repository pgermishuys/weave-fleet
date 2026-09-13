using WeaveFleet.Api.Browser;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// What a browser canvas needs from the user's side: a proxy in front of the page, opening a page or starting a
/// command from the + menu, and the status, output, start, restart and stop of the app Fleet runs for it. Apps
/// are found only through the current user's session.
/// </summary>
public static class BrowserEndpoints
{
    private const int LogLines = 200;

    public static IEndpointRouteBuilder MapBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Browser");

        // POST /api/sessions/{id}/browser/proxy — the preview for a page on this machine, started on first use,
        // at the address this browser can reach it
        group.MapPost("/{id}/browser/proxy", async (
            string id,
            BrowserProxyRequest request,
            HttpContext context,
            SessionService sessionService,
            PreviewGateway gateway) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            if (!LoopbackUrl.TryParse(request.Url, out var target))
                return Results.UnprocessableEntity(new ErrorResponse(LoopbackUrl.Requirement));

            PreviewListener preview;
            try
            {
                preview = await gateway.EnsureAsync(target);
            }
            catch (IOException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }

            return Results.Ok(new BrowserProxyResponse(
                preview.Slug,
                preview.Port,
                preview.Target.ToString(),
                PreviewGateway.OriginFor(context.Request.Host.Host, preview)));
        })
        .Produces<BrowserProxyResponse>(200)
        .Produces(404)
        .Produces(409)
        .Produces(422)
        .WithName("GetBrowserProxy");

        // POST /api/sessions/{id}/browser — show a page on this machine in a browser canvas (the + menu)
        group.MapPost("/{id}/browser", async (string id, BrowserOpenRequest request, SessionService sessionService, BrowserPreviews previews, CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            if (!LoopbackUrl.TryParse(request.Url, out var page))
                return Results.UnprocessableEntity(new ErrorResponse(LoopbackUrl.Requirement));

            var opened = await previews.OpenPageAsync(id, BrowserPreviews.TitleOr(request.Title, page.Authority), page.ToString(), appId: null, ct);
            return opened.IsSuccess
                ? Results.Ok(new BrowserOpenResponse(opened.Value.Canvas.Id))
                : Results.Conflict(new ErrorResponse(opened.Error.Message));
        })
        .Produces<BrowserOpenResponse>(200)
        .Produces(404)
        .Produces(409)
        .Produces(422)
        .WithName("OpenSessionBrowser");

        // GET /api/sessions/{id}/apps — the session's apps, and the command that last served a page in its project
        group.MapGet("/{id}/apps", async (string id, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var list = await apps.ListAsync(id);
            return Results.Ok(new SessionAppsResponse([.. list.Select(run => ToResponse(run, logs: []))], await apps.PreviewCommandAsync(id)));
        })
        .Produces<SessionAppsResponse>(200)
        .Produces(404)
        .WithName("ListSessionApps");

        // POST /api/sessions/{id}/apps — run a command and show it in a browser canvas at once (the + menu).
        // A command the session already runs is left running, and its canvas comes forward.
        group.MapPost("/{id}/apps", async (string id, AppStartRequest request, SessionService sessionService, BrowserPreviews previews, AppRunService apps, CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            var command = request.Command?.Trim();
            if (string.IsNullOrEmpty(command))
                return Results.UnprocessableEntity(new ErrorResponse("\"command\" is required, e.g. \"npm run dev\"."));

            var preview = await previews.StartAppAsync(id, command, BrowserPreviews.TitleOr(request.Title, command), restartLive: false, ct);
            if (!preview.IsSuccess)
                return Results.Conflict(new ErrorResponse(preview.Error.Message));

            var app = preview.Value.Started.App!;
            return Results.Ok(new AppPreviewResponse(ToResponse(app, apps.Logs(app.Id, LogLines)), preview.Value.Canvas.Id));
        })
        .Produces<AppPreviewResponse>(200)
        .Produces(404)
        .Produces(409)
        .Produces(422)
        .WithName("StartSessionApp");

        // GET /api/sessions/{id}/apps/{appId} — status and recent output of an app Fleet runs or ran
        group.MapGet("/{id}/apps/{appId}", async (string id, string appId, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var run = await apps.GetAsync(id, appId);
            return run is null ? AppNotFound(appId) : Results.Ok(ToResponse(run, apps.Logs(run.Id, LogLines)));
        })
        .Produces<AppRunResponse>(200)
        .Produces(404)
        .WithName("GetSessionApp");

        // GET /api/sessions/{id}/apps/{appId}/output?after={n} — output after the first n lines
        group.MapGet("/{id}/apps/{appId}/output", async (string id, string appId, long? after, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var output = await apps.OutputAsync(id, appId, Math.Max(0, after ?? 0));
            return output is null ? AppNotFound(appId) : Results.Ok(new AppOutputResponse(output.Lines, output.Next));
        })
        .Produces<AppOutputResponse>(200)
        .Produces(404)
        .WithName("GetSessionAppOutput");

        // POST /api/sessions/{id}/apps/{appId}/restart — run the same command again, or start a stopped app
        group.MapPost("/{id}/apps/{appId}/restart", async (string id, string appId, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var started = await apps.RestartAsync(id, appId);
            if (started.App is { } run)
                return Results.Ok(ToResponse(run, apps.Logs(run.Id, LogLines)));
            return started.IsNotFound
                ? AppNotFound(appId)
                : Results.Conflict(new ErrorResponse(started.Problem ?? "The app couldn't be started."));
        })
        .Produces<AppRunResponse>(200)
        .Produces(404)
        .Produces(409)
        .WithName("RestartSessionApp");

        // POST /api/sessions/{id}/apps/{appId}/stop — stop the app and its whole process tree
        group.MapPost("/{id}/apps/{appId}/stop", async (string id, string appId, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            return await apps.StopAsync(id, appId) ? Results.NoContent() : AppNotFound(appId);
        })
        .Produces(204)
        .Produces(404)
        .WithName("StopSessionApp");

        return app;
    }

    private static IResult AppNotFound(string appId) => Results.NotFound(new ErrorResponse($"App {appId} not found."));

    private static AppRunResponse ToResponse(AppRunSnapshot run, IReadOnlyList<string> logs)
        => new(
            run.Id,
            run.Command,
            AppRunRecorder.StatusText(run.Status),
            run.ExitCode,
            run.Url,
            run.Ports,
            run.PrintedUrls,
            run.StartedAt,
            logs);
}
#pragma warning restore IL2026
