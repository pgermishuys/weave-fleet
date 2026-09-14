using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class DesktopEndpoints
{
    public static IEndpointRouteBuilder MapDesktopEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/desktop/status — the app asks before closing its window: with sessions working, a Fleet the
        // app started keeps running in the tray instead of stopping them.
        app.MapGet("/api/desktop/status", (SessionActivityTracker activityTracker, IUserContext userContext) =>
            Results.Ok(new DesktopStatusResponse(CountWorking(activityTracker.GetAll().Values, userContext.UserId))))
            .WithTags("Desktop")
            .WithName("GetDesktopStatus");

        return app;
    }

    internal static int CountWorking(IEnumerable<SessionActivitySnapshot> sessions, string userId) =>
        sessions.Count(session =>
            session.ActivityStatus is "busy" or "retry"
            && (session.UserId is null || string.Equals(session.UserId, userId, StringComparison.Ordinal)));
}

#pragma warning restore IL2026
