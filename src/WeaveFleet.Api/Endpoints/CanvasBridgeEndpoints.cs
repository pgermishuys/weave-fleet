using System.Net;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The agent's canvas tools for pooled OpenCode. The <c>fleet-canvas.ts</c> plugin inside each pooled process
/// posts here with <c>Authorization: Bearer {FLEET_BRIDGE_TOKEN}</c> and its OpenCode session id. These routes
/// sit outside Fleet's user auth and CSRF checks: a call is accepted only from a loopback address with a live
/// process token, for a session bound to that process. Every miss is the same 404.
/// </summary>
public static class CanvasBridgeEndpoints
{
    /// <summary>Paths under here authenticate with a process token, never a cookie, so CSRF checks skip them.</summary>
    public const string PathPrefix = "/api/bridge";

    public static IEndpointRouteBuilder MapCanvasBridgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{PathPrefix}/opencode/canvas")
            .AllowAnonymous()
            .WithTags("CanvasBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller());

        group.MapPost("/list", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ListAsync(BridgeToken(http), request.OpenCodeSessionId, ct)))
            .WithName("CanvasBridgeList");

        group.MapPost("/open", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.OpenAsync(BridgeToken(http), request.OpenCodeSessionId, request.Kind, request.Title, request.State, ct)))
            .WithName("CanvasBridgeOpen");

        group.MapPost("/read", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ReadAsync(BridgeToken(http), request.OpenCodeSessionId, request.CanvasId, ct)))
            .WithName("CanvasBridgeRead");

        group.MapPost("/patch", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.PatchAsync(BridgeToken(http), request.OpenCodeSessionId, request.CanvasId, request.Ops, ct)))
            .WithName("CanvasBridgePatch");

        group.MapPost("/focus", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.FocusAsync(BridgeToken(http), request.OpenCodeSessionId, request.CanvasId, ct)))
            .WithName("CanvasBridgeFocus");

        return app;
    }

    private static IResult ToResult(CanvasResult<CanvasToolOutput> result)
    {
        if (result.IsSuccess)
        {
            var output = result.Value;
            return Results.Ok(new CanvasToolResponse(output.Title, output.Output, new CanvasToolMetadata(output.CanvasId, output.Version)));
        }

        var error = new ErrorResponse(result.Error.Message);
        return result.Error.Kind switch
        {
            CanvasErrorKind.NotFound => Results.NotFound(error),
            CanvasErrorKind.Refused => Results.Conflict(error),
            CanvasErrorKind.TooLarge => Results.Json(error, ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status413PayloadTooLarge),
            _ => Results.UnprocessableEntity(error),
        };
    }

    private static IResult UnknownCaller() => Results.NotFound(new ErrorResponse(CanvasBridge.UnknownCallerMessage));

    private static string? BridgeToken(HttpContext http)
    {
        const string scheme = "Bearer ";
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }

    private static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
#pragma warning restore IL2026
