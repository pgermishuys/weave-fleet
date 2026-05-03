using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class SmartLinkEndpoints
{
    public static IEndpointRouteBuilder MapSmartLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions/{sessionId}/smart-links").WithTags("SmartLinks");

        // GET /api/sessions/{sessionId}/smart-links — list active (non-dismissed) links
        group.MapGet("/", async (string sessionId, SmartLinkService smartLinkService) =>
        {
            var links = await smartLinkService.ListBySessionIdAsync(sessionId);
            return Results.Ok(links);
        })
        .Produces<IReadOnlyList<SmartLinkDto>>(200)
        .WithName("GetSmartLinks");

        // GET /api/sessions/{sessionId}/smart-links/all — list all including dismissed (for dedup)
        group.MapGet("/all", async (string sessionId, SmartLinkService smartLinkService) =>
        {
            var links = await smartLinkService.ListAllBySessionIdAsync(sessionId);
            return Results.Ok(links);
        })
        .Produces<IReadOnlyList<SmartLinkDto>>(200)
        .WithName("GetAllSmartLinks");

        // POST /api/sessions/{sessionId}/smart-links — upsert a single link
        group.MapPost("/", async (string sessionId, UpsertSmartLinkRequest request, SmartLinkService smartLinkService) =>
        {
            var link = await smartLinkService.UpsertAsync(sessionId, request);
            if (link is null)
                return Results.NotFound();
            return Results.Ok(link);
        })
        .Produces<SmartLinkDto>(200)
        .WithName("UpsertSmartLink");

        // POST /api/sessions/{sessionId}/smart-links/bulk — bulk upsert
        group.MapPost("/bulk", async (string sessionId, IReadOnlyList<UpsertSmartLinkRequest> requests, SmartLinkService smartLinkService) =>
        {
            var success = await smartLinkService.BulkUpsertAsync(sessionId, requests);
            if (!success)
                return Results.NotFound();
            return Results.Ok();
        })
        .WithName("BulkUpsertSmartLinks");

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
