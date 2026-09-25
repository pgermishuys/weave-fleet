using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Smart links are found and refreshed on the server by the smart link watcher. These endpoints read
/// them and apply the user's choices: attach, pin, dismiss, refresh.
/// </summary>
public static class SmartLinkEndpoints
{
    public static IEndpointRouteBuilder MapSmartLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/smart-links — the links every session header shows (origin, own, pinned), for all sessions
        // that aren't archived: the sessions list's pull request badges and the GitHub pages' session chips.
        app.MapGet("/api/smart-links", async (SmartLinkService smartLinkService, CancellationToken ct) =>
            Results.Ok(await smartLinkService.ListHeaderLinksAsync(ct)))
        .WithTags("SmartLinks")
        .Produces<IReadOnlyList<SmartLinkDto>>(200)
        .WithName("GetHeaderSmartLinks");

        var group = app.MapGroup("/api/sessions/{sessionId}/smart-links").WithTags("SmartLinks");

        // GET /api/sessions/{sessionId}/smart-links — list active (non-dismissed) links
        group.MapGet("/", async (string sessionId, SmartLinkService smartLinkService) =>
        {
            var links = await smartLinkService.ListBySessionIdAsync(sessionId);
            return Results.Ok(links);
        })
        .Produces<IReadOnlyList<SmartLinkDto>>(200)
        .WithName("GetSmartLinks");

        // GET /api/sessions/{sessionId}/smart-links/all — list all including dismissed
        group.MapGet("/all", async (string sessionId, SmartLinkService smartLinkService) =>
        {
            var links = await smartLinkService.ListAllBySessionIdAsync(sessionId);
            return Results.Ok(links);
        })
        .Produces<IReadOnlyList<SmartLinkDto>>(200)
        .WithName("GetAllSmartLinks");

        // POST /api/sessions/{sessionId}/smart-links — attach a GitHub pull request or issue (pinned)
        group.MapPost("/", async (string sessionId, AddSmartLinkRequest request, SmartLinkService smartLinkService, CancellationToken ct) =>
        {
            var result = await smartLinkService.AddAsync(sessionId, request.Url, ct);
            if (result.IsInvalidUrl)
                return Results.BadRequest("Paste a link to a GitHub pull request or issue, like https://github.com/owner/repo/pull/123.");
            return result.Link is null ? Results.NotFound() : Results.Ok(result.Link);
        })
        .Produces<SmartLinkDto>(200)
        .Produces(400)
        .Produces(404)
        .WithName("AddSmartLink");

        // POST /api/sessions/{sessionId}/smart-links/refresh — re-check every link on the session now
        group.MapPost("/refresh", async (string sessionId, SmartLinkService smartLinkService) =>
            await smartLinkService.RefreshAsync(sessionId) ? Results.Accepted() : Results.NotFound())
        .WithName("RefreshSmartLinks");

        // PATCH /api/sessions/{sessionId}/smart-links/{linkId}/pin — show a mentioned link in the header
        group.MapPatch("/{linkId}/pin", async (string sessionId, string linkId, SmartLinkService smartLinkService) =>
            await smartLinkService.SetPinnedAsync(sessionId, linkId, pinned: true) ? Results.NoContent() : Results.NotFound())
        .WithName("PinSmartLink");

        // PATCH /api/sessions/{sessionId}/smart-links/{linkId}/unpin — return a pinned link to the Context tab only
        group.MapPatch("/{linkId}/unpin", async (string sessionId, string linkId, SmartLinkService smartLinkService) =>
            await smartLinkService.SetPinnedAsync(sessionId, linkId, pinned: false) ? Results.NoContent() : Results.NotFound())
        .WithName("UnpinSmartLink");

        // PATCH /api/sessions/{sessionId}/smart-links/{linkId}/dismiss — dismiss a link
        group.MapPatch("/{linkId}/dismiss", async (string sessionId, string linkId, SmartLinkService smartLinkService) =>
        {
            var success = await smartLinkService.DismissAsync(sessionId, linkId);
            if (!success)
                return Results.NotFound();
            return Results.NoContent();
        })
        .WithName("DismissSmartLink");

        return app;
    }
}

#pragma warning restore IL2026
