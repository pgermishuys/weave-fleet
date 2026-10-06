using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Pairing phones and managing the devices that have their own token. See docs/machines.md and docs/phone.md.
/// <list type="bullet">
/// <item><c>POST /api/machine/pairing</c> (owner): a one-time code, as a QR URL and a typeable code.</item>
/// <item><c>POST /api/pairing/preview</c>, <c>POST /api/pairing/redeem</c> (anyone with the code): which machine,
/// then a device token and a device sign-in cookie. Rate limited.</item>
/// <item><c>GET</c>/<c>DELETE /api/machine/devices</c> (owner): the devices with access, and removing one.</item>
/// <item><c>POST /api/machine/devices/me/token</c> (a paired device): a new token for itself, the old one stops.</item>
/// <item><c>POST /api/machine/devices</c> (the machine token only): a device token for a phone paired with another
/// machine, which that machine (its home) asks for on the phone's behalf.</item>
/// </list>
/// Secrets travel only in request bodies, never in a query string, and are never logged.
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>The rate limiter policy for <c>/api/pairing/*</c>.</summary>
    public const string PairingRateLimitPolicy = "pairing";

    private const int MaxDeviceNameLength = 60;
    private static readonly string[] Platforms = ["ios", "android", "other"];

    private const string CodeNotFound = "This code has expired or was already used. Ask for a new one on the computer.";

    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        // Devices exist only in local mode, where whoever holds the machine token owns the machine.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var machine = app.MapGroup("/api/machine").WithTags("Devices");

        machine.MapPost("/pairing", (
            CreatePairingRequest request,
            PairingCodeStore codes,
            MachineIdentityStore identities) =>
        {
            var baseUrl = request.BaseUrl is null ? null : MachineEndpoints.NormalizeBaseUrl(request.BaseUrl.Trim());
            if (baseUrl is null)
                return Results.BadRequest(new ErrorResponse("baseUrl must be the full http:// or https:// address the phone will open."));

            var identity = identities.Get();
            var code = codes.Create();
            var payload = new PairingPayload(1, identity.Id, identity.Name ?? Environment.MachineName, baseUrl, code.Secret);
            var json = JsonSerializer.Serialize(payload, ApiJsonContext.Default.PairingPayload);
            var fragment = Base64Url(Encoding.UTF8.GetBytes(json));

            return Results.Ok(new CreatePairingResponse(code.Secret, code.ManualCode, code.ExpiresAt, $"{baseUrl}/pair#p={fragment}", payload));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<CreatePairingResponse>(200)
        .WithName("CreatePairingCode");

        machine.MapGet("/devices", async (DeviceTokenService devices) =>
            Results.Ok(new DeviceListResponse(await devices.ListAsync())))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<DeviceListResponse>(200)
        .WithName("ListDevices");

        machine.MapDelete("/devices/{id}", async (
            string id,
            DeviceTokenService devices,
            WeaveFleet.Domain.Repositories.IPushSubscriptionRepository subscriptions,
            WeaveFleet.Application.Machines.DeviceGrantService grants,
            CancellationToken cancellationToken) =>
        {
            if (!await devices.RevokeAsync(id))
                return Results.NotFound(new ErrorResponse("No such device."));

            // A removed phone gets no more notifications, and loses the tokens home got it on other machines.
            await subscriptions.DeleteByDeviceAsync(id);
            await grants.RevokeAllAsync(id, cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("RemoveDevice");

        machine.MapPost("/devices", async (HttpContext http, CreateDeviceRequest request, DeviceTokenService devices) =>
        {
            // Another machine asks with the machine token it was given. A browser signed in by cookie (even the
            // owner's) or by loopback doesn't mint tokens: pairing is how a browser adds a device.
            if (!BearerTokenHandler.AuthenticatedWithToken(http.User))
                return Results.Json(new ErrorResponse("Only the machine token can create a device token."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

            if (ValidateDevice(request.Name, request.Platform) is { } invalid)
                return invalid;

            var pairedVia = request.PairedVia?.Trim();
            if (pairedVia is { Length: > 64 })
                return Results.BadRequest(new ErrorResponse("pairedVia is a machine id, at most 64 characters."));

            var (device, token) = await devices.IssueAsync(request.Name!.Trim(), NormalizePlatform(request.Platform), string.IsNullOrEmpty(pairedVia) ? null : pairedVia);
            return Results.Ok(new CreateDeviceResponse(device.Id, token));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<CreateDeviceResponse>(200)
        .WithName("CreateDevice");

        // A paired phone that's signed in but lost its token asks for a new one: the iPhone Home Screen app starts with a
        // copy of Safari's cookies but none of its storage. The old token stops working.
        machine.MapPost("/devices/me/token", async (
            HttpContext http,
            DeviceTokenService devices,
            MachineIdentityStore identities,
            LoopbackAuthPolicy policy) =>
        {
            if (FleetClaims.DeviceIdOf(http.User) is not { } deviceId)
                return Results.BadRequest(new ErrorResponse("Only a paired device has a device token."));
            var token = await devices.ReissueAsync(deviceId);
            return token is null
                ? Results.Unauthorized()
                : Results.Ok(new PairingRedeemResponse(deviceId, token, MachineEndpoints.ToResponse(identities.Get(), fleetOptions, policy)));
        })
        .RequireAuthorization()
        .Produces<PairingRedeemResponse>(200)
        .WithName("ReissueDeviceToken");

        var pairing = app.MapGroup("/api/pairing").WithTags("Devices").RequireRateLimiting(PairingRateLimitPolicy);

        pairing.MapPost("/preview", (
            PairingPreviewRequest request,
            PairingCodeStore codes,
            ManualCodeAttempts attempts,
            MachineIdentityStore identities) =>
        {
            if (Limited(attempts, request.ManualCode) is { } limited)
                return limited;

            var ticket = codes.Peek(request.Secret, request.ManualCode);
            if (ticket is null)
                return Results.NotFound(new ErrorResponse(CodeNotFound));

            var identity = identities.Get();
            return Results.Ok(new PairingPreviewResponse(identity.Id, identity.Name ?? Environment.MachineName, MachineEndpoints.OperatingSystemName(), ticket.ExpiresAt));
        })
        .AllowAnonymous()
        .Produces<PairingPreviewResponse>(200)
        .WithName("PreviewPairingCode");

        pairing.MapPost("/redeem", async (
            HttpContext http,
            PairingRedeemRequest request,
            PairingCodeStore codes,
            ManualCodeAttempts attempts,
            DeviceTokenService devices,
            MachineIdentityStore identities,
            LoopbackAuthPolicy policy) =>
        {
            if (Limited(attempts, request.ManualCode) is { } limited)
                return limited;

            // Check the request before using the code up, so a typo in the name doesn't cost the code.
            if (ValidateDevice(request.DeviceName, request.Platform) is { } invalid)
                return invalid;

            if (codes.TryConsume(request.Secret, request.ManualCode) is null)
                return Results.NotFound(new ErrorResponse(CodeNotFound));

            var (device, token) = await devices.IssueAsync(request.DeviceName!.Trim(), NormalizePlatform(request.Platform));

            // Same-origin features that ride on the cookie (images, pages, the hub) work for the phone at once.
            await LocalSignIn.DeviceAsync(http, device.Id);

            return Results.Ok(new PairingRedeemResponse(device.Id, token, MachineEndpoints.ToResponse(identities.Get(), fleetOptions, policy)));
        })
        .AllowAnonymous()
        .Produces<PairingRedeemResponse>(200)
        .WithName("RedeemPairingCode");

        return app;
    }

    private static IResult? Limited(ManualCodeAttempts attempts, string? manualCode)
    {
        // Any request carrying a typed code counts, with or without a secret beside it: either form finds a code.
        if (string.IsNullOrWhiteSpace(manualCode))
            return null;

        using var lease = attempts.Limiter.AttemptAcquire();
        return lease.IsAcquired
            ? null
            : Results.Json(new ErrorResponse("Too many codes tried. Wait a minute and try again."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static IResult? ValidateDevice(string? name, string? platform)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxDeviceNameLength)
            return Results.BadRequest(new ErrorResponse($"Give the device a name of 1 to {MaxDeviceNameLength} characters."));
        if (platform is not null && !Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse("platform must be ios, android or other."));
        return null;
    }

    private static string NormalizePlatform(string? platform) =>
        platform is null ? "other" : platform.ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
/// <summary>
/// Typed pairing codes are short (40 bits), so trying them gets a tighter limit than the pairing endpoints as a
/// whole: 5 a minute. Behind <c>tailscale serve</c> every caller arrives from 127.0.0.1, so the limit is global.
/// </summary>
public sealed class ManualCodeAttempts : IDisposable
{
    public FixedWindowRateLimiter Limiter { get; } = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 5,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public void Dispose() => Limiter.Dispose();
}
#pragma warning restore IL2026
