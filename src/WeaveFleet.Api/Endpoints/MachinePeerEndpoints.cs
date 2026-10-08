using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Sessions on another machine talking to sessions here, through that machine's Fleet. An agent there calls one of
/// Fleet's tools; its Fleet resolves which session is calling, as it does for a session on the same machine, and
/// calls here with the machine token it keeps for this one. The agent never sees the token.
/// <list type="bullet">
/// <item><c>POST /api/machine/peer/sessions/{id}/message</c>: a message from that session, delivered as
/// <c>fleet_message</c> delivers one, with the sending machine in the tag. Never as the user's own prompt.</item>
/// <item><c>GET /api/machine/peer/sessions/{id}/page</c>: a page of the conversation, as <c>fleet_session_read</c>
/// shows it.</item>
/// </list>
/// Only the machine token may call them: not a paired device, not a browser, not an agent on this machine. So a
/// sender from another machine is only ever named by a Fleet that holds this machine's token, and this machine trusts
/// that name exactly as far as it trusts the token. See docs/machines.md.
/// </summary>
public static class MachinePeerEndpoints
{
    public const string OnlyTheMachineTokenMessage = "Only another machine's Fleet, with this machine's token, can do this.";

    private const int MaxIdLength = 64;
    private const int MaxNameLength = 64;
    private const int MaxTitleLength = 200;

    public static IEndpointRouteBuilder MapMachinePeerEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        // Only local mode has a machine token. MachineCapabilitiesReader says so in peerMessages.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var peer = app.MapGroup("/api/machine/peer/sessions")
            .WithTags("MachinePeer")
            .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
            // The owner policy also lets in the owner's browser (cookie) and loopback; neither names a sender.
            .AddEndpointFilter(async (context, next) => BearerTokenHandler.AuthenticatedWithToken(context.HttpContext.User)
                ? await next(context)
                : Results.Json(new ErrorResponse(OnlyTheMachineTokenMessage), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden));

        peer.MapPost("/{id}/message", async (
            string id,
            PeerSessionMessageRequest request,
            MachineIdentityStore identity,
            SessionService sessions,
            SessionMessageDelivery delivery,
            IUserContext user,
            CancellationToken ct) =>
        {
            if (Invalid(request) is { } invalid)
                return Results.BadRequest(new ErrorResponse(invalid));
            if (request.FromMachineId == identity.Get().Id)
                return Results.BadRequest(new ErrorResponse("fromMachineId is this machine. A session here messages another with fleet_message."));

            var to = await sessions.GetSessionAsync(id);
            if (to.IsFailure)
                return Results.NotFound(new ErrorResponse($"No session {id} here."));

            var sent = await delivery.DeliverAsync(
                request.FromSessionId!.Trim(),
                Clip(request.FromTitle!.Trim(), MaxTitleLength),
                new SessionMessageMachine(request.FromMachineId!.Trim(), request.FromMachineName!.Trim()),
                id,
                request.Text!,
                user.UserId,
                ct);
            return sent.Match(
                receipt => Results.Ok(new PeerSessionMessageResponse(id, to.Value.Title, receipt.MessageId)),
                error => error.ToSessionApiResult());
        })
        .Produces<PeerSessionMessageResponse>(200)
        .WithName("SendPeerSessionMessage");

        peer.MapGet("/{id}/page", async (string id, string? before, int? limit, SessionReadBridge reader, CancellationToken ct) =>
        {
            if (limit is < 1 or > SessionReadBridge.MaxLimit)
                return Results.BadRequest(new ErrorResponse($"limit is from 1 to {SessionReadBridge.MaxLimit}."));

            var page = await reader.ReadPageAsync(id, before, limit, ct);
            return page is null
                ? Results.NotFound(new ErrorResponse($"No session {id} here."))
                : Results.Ok(new PeerSessionPageResponse(page.Title, page.Output));
        })
        .Produces<PeerSessionPageResponse>(200)
        .WithName("ReadPeerSessionPage");

        return app;
    }

    /// <summary>What's wrong with the request, naming the field; null when nothing is.</summary>
    private static string? Invalid(PeerSessionMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FromMachineId) || request.FromMachineId.Trim().Length > MaxIdLength)
            return $"fromMachineId is required, at most {MaxIdLength} characters.";
        if (string.IsNullOrWhiteSpace(request.FromMachineName) || request.FromMachineName.Trim().Length > MaxNameLength)
            return $"fromMachineName is required, at most {MaxNameLength} characters.";
        if (string.IsNullOrWhiteSpace(request.FromSessionId) || request.FromSessionId.Trim().Length > MaxIdLength)
            return $"fromSessionId is required, at most {MaxIdLength} characters.";
        if (string.IsNullOrWhiteSpace(request.FromTitle))
            return "fromTitle is required.";
        if (string.IsNullOrWhiteSpace(request.Text))
            return "text is required.";
        return null;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
#pragma warning restore IL2026
