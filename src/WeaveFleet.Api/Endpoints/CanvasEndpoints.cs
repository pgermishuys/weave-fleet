using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Canvases in a session's right panel. The agent opens and changes them; the user can list and close them.
/// Live changes arrive on <c>session:{id}</c> as <c>canvas.updated</c>, <c>canvas.closed</c> and <c>canvas.focused</c>.
/// </summary>
public static class CanvasEndpoints
{
    public static IEndpointRouteBuilder MapCanvasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Canvases");

        // GET /api/sessions/{id}/canvases — the session's open canvases, oldest first, with their full state
        group.MapGet("/{id}/canvases", async (
            string id,
            SessionService sessionService,
            ICanvasService canvasService,
            CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var canvases = await canvasService.ListAsync(id, ct);
            return Results.Ok(canvases.Select(item => ToResponse(item.Canvas)).ToList());
        })
        .Produces<List<CanvasResponse>>(200)
        .Produces(404)
        .WithName("GetSessionCanvases");

        // DELETE /api/sessions/{id}/canvases/{canvasId} — close a canvas; the agent can open it again by title
        group.MapDelete("/{id}/canvases/{canvasId}", async (
            string id,
            string canvasId,
            SessionService sessionService,
            ICanvasService canvasService,
            CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var closed = await canvasService.CloseAsync(id, canvasId, ct);
            return closed.IsSuccess
                ? Results.NoContent()
                : Results.NotFound(new ErrorResponse($"Canvas {canvasId} not found."));
        })
        .Produces(204)
        .Produces(404)
        .WithName("CloseSessionCanvas");

        // POST /api/sessions/{id}/canvases/{canvasId}/focus — bring a canvas forward, reopening it if it was closed
        // (a tool card's "Show")
        group.MapPost("/{id}/canvases/{canvasId}/focus", async (
            string id,
            string canvasId,
            SessionService sessionService,
            ICanvasService canvasService,
            CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var focused = await canvasService.FocusAsync(id, canvasId, ct);
            return focused.IsSuccess
                ? Results.Ok(ToResponse(focused.Value))
                : Results.NotFound(new ErrorResponse($"Canvas {canvasId} not found."));
        })
        .Produces<CanvasResponse>(200)
        .Produces(404)
        .WithName("FocusSessionCanvas");

        return app;
    }

    private static CanvasResponse ToResponse(Canvas canvas)
    {
        using var state = JsonDocument.Parse(canvas.StateJson);
        return new CanvasResponse(canvas.Id, canvas.Kind, canvas.Title, canvas.Version, state.RootElement.Clone());
    }
}
#pragma warning restore IL2026
