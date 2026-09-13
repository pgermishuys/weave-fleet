using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Terminals in a session's drawer. REST lists, opens and closes them; each open tab attaches over
/// <c>GET …/terminals/{terminalId}/socket</c> (see <see cref="TerminalSocket"/>). Tabs appearing and going away
/// are pushed on <c>session:{id}</c> as <c>terminal.opened</c> and <c>terminal.closed</c>.
/// </summary>
public static class TerminalEndpoints
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;

    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Terminals");

        // GET /api/sessions/{id}/terminals — the session's terminals, oldest first
        group.MapGet("/{id}/terminals", async (
            string id,
            TerminalService terminals,
            CancellationToken ct) =>
        {
            var list = await terminals.ListAsync(id, ct);
            return list.IsSuccess
                ? Results.Ok(list.Value.Select(ToResponse).ToList())
                : ErrorResult(list.Error);
        })
        .Produces<List<TerminalResponse>>(200)
        .Produces<ErrorResponse>(404)
        .WithName("GetSessionTerminals");

        // POST /api/sessions/{id}/terminals — start a shell in the session's folder
        group.MapPost("/{id}/terminals", async (
            string id,
            CreateTerminalRequest? request,
            TerminalService terminals,
            CancellationToken ct) =>
        {
            var created = await terminals.CreateAsync(id, request?.Cols ?? DefaultCols, request?.Rows ?? DefaultRows, ct);
            return created.IsSuccess
                ? Results.Created($"/api/sessions/{id}/terminals/{created.Value.Id}", ToResponse(created.Value))
                : ErrorResult(created.Error);
        })
        .Produces<TerminalResponse>(201)
        .Produces<ErrorResponse>(404)
        .Produces<ErrorResponse>(409)
        .Produces<ErrorResponse>(500)
        .WithName("CreateSessionTerminal");

        // DELETE /api/sessions/{id}/terminals/{terminalId} — end the shell and delete its scrollback
        group.MapDelete("/{id}/terminals/{terminalId}", async (
            string id,
            string terminalId,
            TerminalService terminals,
            CancellationToken ct) =>
        {
            var error = await terminals.CloseAsync(id, terminalId, ct);
            return error is null ? Results.NoContent() : ErrorResult(error);
        })
        .Produces(204)
        .Produces<ErrorResponse>(404)
        .WithName("CloseSessionTerminal");

        // GET /api/sessions/{id}/terminals/{terminalId}/socket — attach: scrollback, then live output; input back
        group.MapGet("/{id}/terminals/{terminalId}/socket", async (
            HttpContext http,
            string id,
            string terminalId,
            int? cols,
            int? rows,
            TerminalService terminals,
            FleetOptions options,
            IHostEnvironment environment) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new ErrorResponse("Connect to this address with a WebSocket."));
            if (!TerminalSocket.IsOriginAllowed(http, options, environment.IsDevelopment()))
                return Results.Json(new ErrorResponse("This page isn't allowed to open terminals."), ApiJsonContext.Default.ErrorResponse, statusCode: 403);

            var attached = await terminals.AttachAsync(id, terminalId, cols ?? DefaultCols, rows ?? DefaultRows, http.RequestAborted);
            if (!attached.IsSuccess)
                return ErrorResult(attached.Error);

            using var attachment = attached.Value;
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await TerminalSocket.RunAsync(socket, attachment, http.RequestAborted);
            return Results.Empty;
        })
        .ExcludeFromDescription();

        return app;
    }

    private static TerminalResponse ToResponse(TerminalInfo terminal) => new(
        terminal.Id,
        terminal.Title,
        terminal.Status == TerminalStatus.Running ? "running" : "stopped",
        terminal.CreatedAt);

    private static IResult ErrorResult(TerminalError error)
    {
        var status = error.Kind switch
        {
            TerminalErrorKind.NotFound => 404,
            TerminalErrorKind.Unavailable => 409,
            TerminalErrorKind.LimitReached => 409,
            _ => 500,
        };
        return Results.Json(new ErrorResponse(error.Message), ApiJsonContext.Default.ErrorResponse, statusCode: status);
    }
}
#pragma warning restore IL2026
