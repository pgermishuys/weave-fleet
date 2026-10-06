using Microsoft.AspNetCore.Mvc;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Push;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Where this machine sends notifications: a browser registers its Web Push subscription, picks which kinds it
/// wants, tests it, and removes it. The endpoint is a capability URL, so it only ever travels in request bodies.
/// A paired device manages only its own subscriptions; the owner manages any.
/// </summary>
public static class PushEndpoints
{
    private const int MaxEndpointLength = 2048;

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var group = app.MapGroup("/api/push").WithTags("Push");

        group.MapGet("/vapid", (VapidKeyStore keys) => Results.Ok(new VapidKeyResponse(keys.Get().PublicKey)))
            .Produces<VapidKeyResponse>(200)
            .WithName("GetVapidKey");

        group.MapPut("/subscriptions", async (
            HttpContext http,
            SavePushSubscriptionRequest request,
            IPushSubscriptionRepository subscriptions,
            TimeProvider time) =>
        {
            if (ValidateEndpoint(request.Endpoint) is { } badEndpoint)
                return badEndpoint;
            if (!ValidKey(request.Keys?.P256dh, 65) || !ValidKey(request.Keys?.Auth, 16))
                return Results.BadRequest(new ErrorResponse("keys.p256dh and keys.auth must be the browser's subscription keys."));
            if (request.Kinds is { } kinds && kinds.Any(kind => !SessionNotificationKinds.IsKnown(kind)))
                return Results.BadRequest(new ErrorResponse($"kinds may only hold {string.Join(", ", SessionNotificationKinds.All)}."));
            var channel = request.Channel ?? "webpush";
            if (channel != "webpush")
                return Results.BadRequest(new ErrorResponse("channel must be webpush."));

            var existing = await subscriptions.GetByEndpointAsync(request.Endpoint!);
            if (existing is not null && !MayManage(http, existing))
                return Forbidden();

            // A rotated subscription carries over the settings of the one it replaces, if the caller owns that one.
            PushSubscriptionRecord? previous = null;
            if (!string.IsNullOrEmpty(request.PreviousEndpoint) && request.PreviousEndpoint != request.Endpoint)
            {
                previous = await subscriptions.GetByEndpointAsync(request.PreviousEndpoint);
                if (previous is not null && !MayManage(http, previous))
                    previous = null;
            }

            var settingsFrom = existing ?? previous;
            var saved = await subscriptions.UpsertAsync(new PushSubscriptionRecord
            {
                Id = existing?.Id ?? Ulid.NewUlid().ToString(),
                DeviceId = FleetClaims.DeviceIdOf(http.User) ?? existing?.DeviceId,
                Channel = channel,
                Endpoint = request.Endpoint!,
                P256dh = request.Keys!.P256dh!,
                Auth = request.Keys.Auth!,
                Kinds = request.Kinds?.Distinct().ToList() ?? settingsFrom?.Kinds ?? SessionNotificationKinds.Default,
                QuietWhenDesk = request.QuietWhenDesk ?? settingsFrom?.QuietWhenDesk ?? true,
                UserAgent = Clip(http.Request.Headers.UserAgent.ToString(), 200),
                CreatedAt = time.GetUtcNow(),
            });

            if (previous is not null)
                await subscriptions.DeleteByEndpointAsync(previous.Endpoint);

            return Results.Ok(ToResponse(saved));
        })
        .Produces<PushSubscriptionResponse>(200)
        .WithName("SavePushSubscription");

        group.MapPost("/subscriptions/lookup", async (HttpContext http, PushEndpointRequest request, IPushSubscriptionRepository subscriptions) =>
        {
            var found = request.Endpoint is null ? null : await subscriptions.GetByEndpointAsync(request.Endpoint);
            if (found is null || !MayManage(http, found))
                return Results.NotFound(new ErrorResponse("This browser isn't subscribed."));
            return Results.Ok(ToResponse(found));
        })
        .Produces<PushSubscriptionResponse>(200)
        .WithName("LookUpPushSubscription");

        group.MapDelete("/subscriptions", async (HttpContext http, [FromBody] PushEndpointRequest request, IPushSubscriptionRepository subscriptions) =>
        {
            var found = request.Endpoint is null ? null : await subscriptions.GetByEndpointAsync(request.Endpoint);
            if (found is null)
                return Results.NoContent();
            if (!MayManage(http, found))
                return Forbidden();
            await subscriptions.DeleteByEndpointAsync(found.Endpoint);
            return Results.NoContent();
        })
        .WithName("DeletePushSubscription");

        group.MapPost("/test", async (
            HttpContext http,
            PushEndpointRequest request,
            IPushSubscriptionRepository subscriptions,
            IEnumerable<IPushSender> senders,
            MachineIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var found = request.Endpoint is null ? null : await subscriptions.GetByEndpointAsync(request.Endpoint);
            if (found is null || !MayManage(http, found))
                return Results.NotFound(new ErrorResponse("This browser isn't subscribed."));

            // The last sender registered for a channel wins, as DI does for single services.
            var sender = senders.LastOrDefault(s => s.Channel == found.Channel);
            if (sender is null)
                return Results.BadRequest(new ErrorResponse($"This Fleet can't send over {found.Channel}."));

            var identity = identities.Get();
            var machineName = identity.Name ?? Environment.MachineName;
            var payload = new PushPayload(
                1, identity.Id, machineName, "test", SessionNotificationKinds.Finished, SessionNotificationReasons.Finished,
                "Fleet notifications work", $"This is a test from {machineName}.", "/phone", $"{identity.Id}:test");
            var result = await sender.SendAsync(found, new PushMessage(payload.ToJson(), Urgent: true, PushMessage.DefaultTimeToLive), cancellationToken);

            if (result.Outcome == PushSendOutcome.Gone)
                await subscriptions.DeleteByEndpointAsync(found.Endpoint);
            else if (result.Outcome == PushSendOutcome.Delivered)
                await subscriptions.RecordSuccessAsync(found.Id, DateTimeOffset.UtcNow);

            return Results.Ok(new PushTestResponse(result.Outcome switch
            {
                PushSendOutcome.Delivered => "delivered",
                PushSendOutcome.Gone => "gone",
                PushSendOutcome.RetryLater => "retry_later",
                _ => "failed",
            }, result.StatusCode));
        })
        .Produces<PushTestResponse>(200)
        .WithName("SendTestPush");

        return app;
    }

    /// <summary>The owner manages every subscription; a device only its own.</summary>
    private static bool MayManage(HttpContext http, PushSubscriptionRecord subscription)
    {
        if (FleetClaims.IsOwner(http.User))
            return true;
        var deviceId = FleetClaims.DeviceIdOf(http.User);
        return deviceId is not null && subscription.DeviceId == deviceId;
    }

    private static IResult Forbidden() =>
        Results.Json(new ErrorResponse("This subscription belongs to another device."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

    private static IResult? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Results.BadRequest(new ErrorResponse("endpoint must be the subscription's https:// address."));
        // Fleet posts to this address, so it must be a push service on the internet: those always have a DNS name.
        // A bare IP or a local name would point Fleet at this machine's own network instead.
        if (uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || !uri.Host.Contains('.', StringComparison.Ordinal)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse("endpoint must be a push service's address."));
        return null;
    }

    private static bool ValidKey(string? value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;
        try
        {
            return Base64Url.Decode(value).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Clip(string value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    private static PushSubscriptionResponse ToResponse(PushSubscriptionRecord subscription) =>
        new(subscription.Channel, subscription.Kinds, subscription.QuietWhenDesk, subscription.CreatedAt, subscription.LastSuccessAt);
}
#pragma warning restore IL2026
