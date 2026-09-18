using System.Net;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The agent's canvas tools, for any harness process Fleet runs. The harness's side posts here with
/// <c>Authorization: Bearer {FLEET_BRIDGE_TOKEN}</c> and the harness's own session id, which
/// <see cref="IHarnessCanvasCallerResolver"/> turns into a Fleet session. These routes sit outside Fleet's user
/// auth and CSRF checks: a call is accepted only from a loopback address with a live process token, for a session
/// bound to that process. Every miss is the same 404.
/// </summary>
public static class CanvasBridgeEndpoints
{
    /// <summary>Paths under here authenticate with a process token, never a cookie, so CSRF checks skip them.</summary>
    public const string PathPrefix = "/api/bridge";

    public static IEndpointRouteBuilder MapCanvasBridgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{PathPrefix}/canvas")
            .AllowAnonymous()
            .WithTags("CanvasBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller());

        group.MapPost("/list", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ListAsync(BridgeToken(http), request.HarnessSessionId, ct)))
            .WithName("CanvasBridgeList");

        group.MapPost("/open", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.OpenAsync(BridgeToken(http), request.HarnessSessionId, request.Kind, request.Title, request.State, ct)))
            .WithName("CanvasBridgeOpen");

        group.MapPost("/read", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ReadAsync(BridgeToken(http), request.HarnessSessionId, request.CanvasId, ct)))
            .WithName("CanvasBridgeRead");

        group.MapPost("/patch", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.PatchAsync(BridgeToken(http), request.HarnessSessionId, request.CanvasId, request.Ops, ct)))
            .WithName("CanvasBridgePatch");

        group.MapPost("/focus", async (CanvasBridgeRequest request, HttpContext http, CanvasBridge bridge, CancellationToken ct)
            => ToResult(await bridge.FocusAsync(BridgeToken(http), request.HarnessSessionId, request.CanvasId, ct)))
            .WithName("CanvasBridgeFocus");

        group.MapPost("/app-start", async (CanvasBridgeRequest request, HttpContext http, BrowserBridge bridge, CancellationToken ct)
            => ToResult(await bridge.AppStartAsync(BridgeToken(http), request.HarnessSessionId, request.Command, request.Title, ct)))
            .WithName("CanvasBridgeAppStart");

        group.MapPost("/browser-open", async (CanvasBridgeRequest request, HttpContext http, BrowserBridge bridge, CancellationToken ct)
            => ToResult(await bridge.BrowserOpenAsync(BridgeToken(http), request.HarnessSessionId, request.Url, request.Title, ct)))
            .WithName("CanvasBridgeBrowserOpen");

        group.MapPost("/screenshot", async (CanvasBridgeRequest request, HttpContext http, BrowserBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ScreenshotAsync(BridgeToken(http), request.HarnessSessionId, request.CanvasId, request.Path, request.Viewport, ct)))
            .WithName("CanvasBridgeScreenshot");

        // fleet_message: one session's agent messages another. The sender is the session the call resolves to.
        app.MapGroup($"{PathPrefix}/session")
            .AllowAnonymous()
            .WithTags("SessionMessageBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller())
            .MapPost("/message", async (SessionMessageBridgeRequest request, HttpContext http, SessionMessageBridge bridge, CancellationToken ct)
                => ToResult(await bridge.SendAsync(BridgeToken(http), request.HarnessSessionId, request.SessionId, request.Text, ct)))
            .WithName("SessionMessageBridgeSend");

        return app;
    }

    private static IResult ToResult(CanvasResult<CanvasToolOutput> result)
    {
        if (result.IsSuccess)
        {
            var output = result.Value;
            var attachments = output.Attachments?
                .Select(file => new CanvasToolAttachmentResponse(file.Mime, file.FileName, Convert.ToBase64String(file.Content)))
                .ToList();
            return Results.Ok(new CanvasToolResponse(output.Title, output.Output, new CanvasToolMetadata(output.CanvasId, output.Version), attachments));
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
