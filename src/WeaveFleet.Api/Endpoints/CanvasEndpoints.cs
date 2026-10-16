using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Sessions;
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

        // POST /api/sessions/{id}/page-choices/{callId}/send — the user sends the pick they made on a waiting page, from
        // Fleet's bar under it. POST …/skip ends the wait without one. Only the user: an agent can't answer its own page.
        group.MapPost("/{id}/page-choices/{callId}/send", (string id, string callId, PageChoiceSendRequest request, HttpContext http, SessionService sessionService, PageChoices choices)
            => request.Value?.Trim() is not { Length: > 0 and <= MaxChoiceValue } value
                ? Task.FromResult(Results.BadRequest(new ErrorResponse($"\"value\" must be 1-{MaxChoiceValue} characters.")))
                : EndChoiceAsync(id, callId, http, sessionService, choices,
                    PageChoiceAnswer.Picked(value, request.Summary?.Trim() is { Length: > 0 } summary ? Shorten(summary) : Shorten(value))))
        .WithName("SendPageChoice");

        group.MapPost("/{id}/page-choices/{callId}/skip", (string id, string callId, HttpContext http, SessionService sessionService, PageChoices choices)
            => EndChoiceAsync(id, callId, http, sessionService, choices, PageChoiceAnswer.NotAnswered(PageChoiceReasons.Skipped)))
        .WithName("SkipPageChoice");

        return app;
    }

    private const int MaxChoiceValue = 1000;

    private static string Shorten(string summary) => summary.Length <= 200 ? summary : summary[..200];

    private static async Task<IResult> EndChoiceAsync(
        string id, string callId, HttpContext http, SessionService sessionService, PageChoices choices, PageChoiceAnswer answer)
    {
        if (http.IsAgentRequest())
            return Results.Json(new ErrorResponse("Only the user answers a page."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

        var session = await sessionService.GetSessionAsync(id);
        if (session.IsFailure)
            return session.Error.ToSessionApiResult();

        return choices.End(id, callId, answer)
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse("This page isn't waiting for a pick anymore."));
    }

    private static CanvasResponse ToResponse(Canvas canvas)
    {
        using var state = JsonDocument.Parse(canvas.StateJson);
        return new CanvasResponse(canvas.Id, canvas.Kind, canvas.Title, canvas.Version, state.RootElement.Clone());
    }
}
#pragma warning restore IL2026
