using WeaveFleet.Api.Browser;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// What a browser canvas needs from the user's side: a proxy in front of the page, and the status, output,
/// restart and stop of the app Fleet runs for it.
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

        // GET /api/sessions/{id}/apps/{appId} — status and recent output of an app Fleet runs
        group.MapGet("/{id}/apps/{appId}", async (string id, string appId, SessionService sessionService, IAppRunner apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var run = apps.Find(appId);
            return run is null || run.SessionId != id
                ? Results.NotFound(new ErrorResponse($"App {appId} not found."))
                : Results.Ok(ToResponse(run, apps));
        })
        .Produces<AppRunResponse>(200)
        .Produces(404)
        .WithName("GetSessionApp");

        // POST /api/sessions/{id}/apps/{appId}/restart — stop the app and run the same command again
        group.MapPost("/{id}/apps/{appId}/restart", async (string id, string appId, SessionService sessionService, IAppRunner apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            if (apps.Find(appId) is not { } run || run.SessionId != id)
                return Results.NotFound(new ErrorResponse($"App {appId} not found."));

            var restarted = await apps.RestartAsync(appId);
            return restarted is null ? Results.NotFound(new ErrorResponse($"App {appId} not found.")) : Results.Ok(ToResponse(restarted, apps));
        })
        .Produces<AppRunResponse>(200)
        .Produces(404)
        .WithName("RestartSessionApp");

        // POST /api/sessions/{id}/apps/{appId}/stop — stop the app and its whole process tree
        group.MapPost("/{id}/apps/{appId}/stop", async (string id, string appId, SessionService sessionService, IAppRunner apps) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();
            if (apps.Find(appId) is not { } run || run.SessionId != id)
                return Results.NotFound(new ErrorResponse($"App {appId} not found."));

            await apps.StopAsync(appId);
            return Results.NoContent();
        })
        .Produces(204)
        .Produces(404)
        .WithName("StopSessionApp");

        return app;
    }

    private static AppRunResponse ToResponse(AppRunSnapshot run, IAppRunner apps)
        => new(
            run.Id,
            run.Command,
            run.Status.ToString().ToLowerInvariant(),
            run.ExitCode,
            run.Url,
            run.Ports,
            run.PrintedUrls,
            run.StartedAt,
            apps.Logs(run.Id, LogLines));
}
#pragma warning restore IL2026
