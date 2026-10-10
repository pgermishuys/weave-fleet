using System.Net;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Walkthroughs;
using WeaveFleet.Application.Workflows;

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

        group.MapPost("/browser-read", async (AgentBrowserToolRequest request, HttpContext http, AgentBrowserBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ReadAsync(BridgeToken(http), request, ct)))
            .WithName("CanvasBridgeBrowserRead");

        group.MapPost("/browser-act", async (AgentBrowserToolRequest request, HttpContext http, AgentBrowserBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ActAsync(BridgeToken(http), request, ct)))
            .WithName("CanvasBridgeBrowserAct");

        group.MapPost("/page-show", async (CanvasBridgeRequest request, HttpContext http, PageBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ShowAsync(BridgeToken(http), request.HarnessSessionId, request.Path, request.Title, request.Placement, ct)))
            .WithName("CanvasBridgePageShow");

        group.MapPost("/walkthrough-show", async (WalkthroughBridgeRequest request, HttpContext http, WalkthroughBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ShowAsync(BridgeToken(http), request.HarnessSessionId, request.Title, request.Guide, ct)))
            .WithName("CanvasBridgeWalkthroughShow");

        var session = app.MapGroup($"{PathPrefix}/session")
            .AllowAnonymous()
            .WithTags("SessionMessageBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller());

        // fleet_message: one session's agent messages another. The sender is the session the call resolves to.
        session.MapPost("/message", async (SessionMessageBridgeRequest request, HttpContext http, SessionMessageBridge bridge, CancellationToken ct)
                => ToResult(await bridge.SendAsync(BridgeToken(http), request.HarnessSessionId, request.SessionId, request.Text, request.NotifyWhenDone, request.Machine, ct)))
            .WithName("SessionMessageBridgeSend");

        // fleet_session_read: a page of a session the user @-referenced, for the agent the reference went to.
        session.MapPost("/read", async (SessionReadBridgeRequest request, HttpContext http, SessionReadBridge bridge, CancellationToken ct)
                => ToResult(await bridge.ReadAsync(BridgeToken(http), request.HarnessSessionId, request.SessionId, request.Before, request.Limit, request.Machine, ct)))
            .WithName("SessionReadBridgeRead");

        // fleet_machine_list / fleet_session_start: an agent hands work to another machine (agent hand-off). This Fleet
        // makes the calls there, with the token it keeps for the machine.
        session.MapPost("/machines", async (CanvasBridgeRequest request, HttpContext http, MachineHandoffBridge bridge, CancellationToken ct)
                => ToResult(await bridge.ListAsync(BridgeToken(http), request.HarnessSessionId, ct)))
            .WithName("MachineHandoffBridgeList");

        session.MapPost("/start", async (SessionStartBridgeRequest request, HttpContext http, MachineHandoffBridge bridge, CancellationToken ct)
                => ToResult(await bridge.StartAsync(
                    BridgeToken(http), request.HarnessSessionId, request.Machine, request.Folder, request.Title, request.Task, request.Branch, request.Harness, request.NotifyWhenDone, ct)))
            .WithName("MachineHandoffBridgeStart");

        // fleet_step_done: a workflow step's session finishes the step. Only the step's own session can.
        app.MapGroup($"{PathPrefix}/workflow")
            .AllowAnonymous()
            .WithTags("WorkflowStepBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller())
            .MapPost("/step-done", async (WorkflowStepBridgeRequest request, HttpContext http, WorkflowStepBridge bridge, CancellationToken ct)
                => ToResult(await bridge.DoneAsync(BridgeToken(http), request.HarnessSessionId, request.Outcome, request.Summary, ct)))
            .WithName("WorkflowStepBridgeDone");

        // fleet_memory_save / fleet_memory_forget: notes for the session's repository or this machine.
        var memory = app.MapGroup($"{PathPrefix}/memory")
            .AllowAnonymous()
            .WithTags("MemoryBridge")
            .AddEndpointFilter(async (context, next) =>
                IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
                    ? await next(context)
                    : UnknownCaller());

        memory.MapPost("/save", async (MemoryBridgeRequest request, HttpContext http, AgentMemoryBridge bridge, CancellationToken ct)
            => ToResult(await bridge.SaveAsync(BridgeToken(http), request.HarnessSessionId, request.List, request.Text, request.Kind, request.Replaces, ct)))
            .WithName("MemoryBridgeSave");

        memory.MapPost("/forget", async (MemoryBridgeRequest request, HttpContext http, AgentMemoryBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ForgetAsync(BridgeToken(http), request.HarnessSessionId, request.Id, ct)))
            .WithName("MemoryBridgeForget");

        // fleet_mod_write / _check / _reload / _test / _keep / _list: the agent's draft mods in its own session. Configured
        // statement by statement, like the groups above that end in a filter lambda: a chain ending in one broke the AOT build.
        var mods = app.MapGroup($"{PathPrefix}/mods");
        mods.AllowAnonymous();
        mods.WithTags("ModBridge");
        mods.AddEndpointFilter(LoopbackOnlyAsync);

        mods.MapPost("/write", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.WriteAsync(
                BridgeToken(http),
                request.HarnessSessionId,
                request.Name,
                request.Files?.Select(f => f.Path is { } path && f.Content is { } content ? new ModFile(path, content) : new ModFile("", "")).ToList(),
                ct)))
            .WithName("ModBridgeWrite");

        mods.MapPost("/check", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.CheckAsync(BridgeToken(http), request.HarnessSessionId, request.Name, ct)))
            .WithName("ModBridgeCheck");

        mods.MapPost("/reload", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ReloadAsync(BridgeToken(http), request.HarnessSessionId, request.Name, ct)))
            .WithName("ModBridgeReload");

        mods.MapPost("/test", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.TestAsync(BridgeToken(http), request.HarnessSessionId, request.Name, request.Event, request.E ?? default, ct)))
            .WithName("ModBridgeTest");

        mods.MapPost("/keep", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.KeepAsync(BridgeToken(http), request.HarnessSessionId, request.Name, request.Note, ct)))
            .WithName("ModBridgeKeep");

        mods.MapPost("/list", async (ModBridgeRequest request, HttpContext http, ModBridge bridge, CancellationToken ct)
            => ToResult(await bridge.ListAsync(BridgeToken(http), request.HarnessSessionId, ct)))
            .WithName("ModBridgeList");

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
            var screenshot = output.Screenshot is { } shot
                ? new CanvasToolScreenshotMetadata(shot.SessionId, shot.Id, shot.Width, shot.Height)
                : null;
            var page = output.Page is { } shown ? new CanvasToolPageMetadata(shown.Id, shown.Entry) : null;
            return Results.Ok(new CanvasToolResponse(output.Title, output.Output, new CanvasToolMetadata(output.CanvasId, output.Version, screenshot, page), attachments));
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

    private static async ValueTask<object?> LoopbackOnlyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => IsLoopback(context.HttpContext.Connection.RemoteIpAddress)
            ? await next(context)
            : UnknownCaller();

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
