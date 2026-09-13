using WeaveFleet.Api.Browser;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// What a browser canvas needs from the user's side: a proxy in front of the page, and the status, output,
/// start, restart and stop of the app Fleet runs for it. Apps are found only through the current user's session.
/// </summary>
public static class BrowserEndpoints
{
    private const int LogLines = 200;

    public static IEndpointRouteBuilder MapBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Browser");

        // POST /api/sessions/{id}/browser/proxy — the proxy for a page on this machine, started on first use
        group.MapPost("/{id}/browser/proxy", async (
            string id,
            BrowserProxyRequest request,
            SessionService sessionService,
            PreviewProxies proxies) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            if (!LoopbackUrl.TryParse(request.Url, out var target))
                return Results.UnprocessableEntity(new ErrorResponse(LoopbackUrl.Requirement));

            var proxy = await proxies.EnsureAsync(target);
            return Results.Ok(new BrowserProxyResponse(proxy.Slug, proxy.Port, proxy.Target.ToString()));
        })
        .Produces<BrowserProxyResponse>(200)
        .Produces(404)
        .Produces(422)
        .WithName("GetBrowserProxy");

        // GET /api/sessions/{id}/apps/{appId} — status and recent output of an app Fleet runs or ran
        group.MapGet("/{id}/apps/{appId}", async (string id, string appId, SessionService sessionService, AppRunService apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var run = await apps.GetAsync(id, appId);
            return run is null ? AppNotFound(appId) : Results.Ok(ToResponse(run, apps));
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
                return Results.Ok(ToResponse(run, apps));
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

    private static AppRunResponse ToResponse(AppRunSnapshot run, AppRunService apps)
        => new(
            run.Id,
            run.Command,
            AppRunRecorder.StatusText(run.Status),
            run.ExitCode,
            run.Url,
            run.Ports,
            run.PrintedUrls,
            run.StartedAt,
            apps.Logs(run.Id, LogLines));
}
#pragma warning restore IL2026
