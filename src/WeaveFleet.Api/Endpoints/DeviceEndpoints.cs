using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;

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

    private const string CodeNotFound = "This code has expired or was already used. Ask for a new one in Fleet on the machine.";

    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        // Devices exist only in local mode, where whoever holds the machine token owns the machine.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var machine = app.MapGroup("/api/machine").WithTags("Devices");
        machine.AddEndpointFilter<AgentRequests.WritesAreTheUsers>();

        machine.MapPost("/pairing", async (
            CreatePairingRequest request,
            PairingCodeStore codes,
            MachineIdentityStore identities,
            LoopbackAuthPolicy policy,
            TailnetName tailnet,
            CancellationToken ct) =>
        {
            // A machine with no web app makes phone codes too: the native phone app redeems them over the API, and the
            // code carries every address, so nobody has to say which one the phone opens.
            var identity = identities.Get();
            var reachable = policy.IsRemoteReachable
                ? MachineEndpoints.ReachableAddresses(fleetOptions, await tailnet.GetAsync(ct)).Select(address => address.Url)
                : [];

            string? own = null;
            if (!string.IsNullOrWhiteSpace(request.BaseUrl))
            {
                own = MachineEndpoints.NormalizeBaseUrl(request.BaseUrl.Trim());
                if (own is null)
                    return Results.BadRequest(new ErrorResponse("baseUrl must be the full http:// or https:// address the phone will open."));
            }

            var urls = PairingAddresses.For(own, identity.PublicUrl, reachable);
            if (urls.Count == 0)
                return Results.BadRequest(new ErrorResponse(
                    "Fleet only listens on this computer, so no phone can reach it. Let other devices reach it first, or give an address of your own."));

            var code = codes.Create();
            var payload = new PairingPayload(1, identity.Id, identity.Name ?? Environment.MachineName, urls[0], code.Secret, urls);
            return Results.Ok(new CreatePairingResponse(code.Secret, code.ManualCode, code.ExpiresAt, PairingAddresses.Link(payload), payload));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<CreatePairingResponse>(200)
        .WithName("CreatePairingCode");

        // A code for another Fleet (kind "machine"), made by `fleet node pair` on the machine itself. Unlike the phone
        // code it needs no web app, so a node can make one.
        machine.MapPost("/pairing/code", async (
            PairingCodeStore codes,
            MachineIdentityStore identities,
            LoopbackAuthPolicy policy,
            TailnetName tailnet,
            CancellationToken ct) =>
        {
            var identity = identities.Get();
            var code = codes.Create();
            var addresses = policy.IsRemoteReachable
                ? MachineEndpoints.ReachableAddresses(fleetOptions, await tailnet.GetAsync(ct), policy.Reach).Select(address => address.Url).ToList()
                : [];
            // The same code opens a phone too: the QR a machine with no screen prints carries every address.
            var phoneUrls = PairingAddresses.For(null, identity.PublicUrl, addresses);
            var name = identity.Name ?? Environment.MachineName;
            var phoneLink = phoneUrls.Count == 0 ? null : PairingAddresses.Link(new PairingPayload(1, identity.Id, name, phoneUrls[0], code.Secret, phoneUrls));
            return Results.Ok(new MachinePairingCodeResponse(code.ManualCode, code.ExpiresAt, identity.Id, name, addresses, MachineEndpoints.PairingLink(addresses, code.ManualCode), phoneLink));
        })
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<MachinePairingCodeResponse>(200)
        .WithName("CreateMachinePairingCode");

        machine.MapGet("/devices", async (DeviceTokenService devices) =>
            Results.Ok(new DeviceListResponse(await devices.ListAsync())))
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .Produces<DeviceListResponse>(200)
        .WithName("ListDevices");

        machine.MapDelete("/devices/{id}", async (
            HttpContext http,
            string id,
            DeviceTokenService devices,
            WeaveFleet.Domain.Repositories.IPushSubscriptionRepository subscriptions,
            WeaveFleet.Application.Machines.DeviceGrantService grants,
            CancellationToken cancellationToken) =>
        {
            // A machine in the owner's Fleet (it holds a machine-kind token) removes only what a machine made for a phone
            // or screen: a device with a home. Everything else is the owner's.
            if (!FleetClaims.IsOwner(http.User))
            {
                var target = (await devices.ListAsync()).FirstOrDefault(d => d.Id == id);
                if (!await IsPeerMachineAsync(http, devices) || target is null || target.PairedVia is null)
                    return Results.Json(new ErrorResponse("Only the owner removes a device."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!await devices.RevokeAsync(id))
                return Results.NotFound(new ErrorResponse("No such device."));

            // A removed phone gets no more notifications, and loses the tokens home got it on other machines.
            await subscriptions.DeleteByDeviceAsync(id);
            await grants.RevokeAllAsync(id, cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("RemoveDevice");

        machine.MapPost("/devices", async (HttpContext http, CreateDeviceRequest request, DeviceTokenService devices) =>
        {
            // Another machine asks with the machine token it was given, or with the machine-kind device token it got by
            // pairing (that's how a machine added by its pairing code asks). A browser signed in by cookie (even the
            // owner's) or by loopback doesn't mint tokens: pairing is how a browser adds a device.
            if (!BearerTokenHandler.AuthenticatedWithToken(http.User) || !(FleetClaims.IsOwner(http.User) || await IsPeerMachineAsync(http, devices)))
                return Results.Json(new ErrorResponse("Only the machine token can create a device token."), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

            if (ValidateDevice(request.Name, request.Platform) is { } invalid)
                return invalid;

            var pairedVia = request.PairedVia?.Trim();
            if (pairedVia is { Length: > 64 })
                return Results.BadRequest(new ErrorResponse("pairedVia is a machine id, at most 64 characters."));

            var (device, token) = await devices.IssueAsync(request.Name!.Trim(), NormalizePlatform(request.Platform), string.IsNullOrEmpty(pairedVia) ? null : pairedVia);
            return Results.Ok(new CreateDeviceResponse(device.Id, token));
        })
        .RequireAuthorization()
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
            return Results.Ok(new PairingPreviewResponse(identity.Id, identity.Name ?? Environment.MachineName, MachineEndpoints.OperatingSystemName(), ticket.ExpiresAt, FleetInstrumentation.ServiceVersion.Split('+')[0]));
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
            var isMachine = string.Equals(request.Kind, DeviceKinds.Machine, StringComparison.OrdinalIgnoreCase);
            if (request.Kind is not null && !isMachine && !string.Equals(request.Kind, DeviceKinds.Device, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ErrorResponse("kind must be device or machine."));

            if (codes.TryConsume(request.Secret, request.ManualCode) is null)
                return Results.NotFound(new ErrorResponse(CodeNotFound));

            var (device, token) = await devices.IssueAsync(request.DeviceName!.Trim(), NormalizePlatform(request.Platform), kind: isMachine ? DeviceKinds.Machine : DeviceKinds.Device);

            // Same-origin features that ride on the cookie (images, pages, the hub) work for the phone at once. A
            // machine talks with its token and never holds a browser session.
            if (!isMachine)
                await LocalSignIn.DeviceAsync(http, device.Id);

            return Results.Ok(new PairingRedeemResponse(device.Id, token, MachineEndpoints.ToResponse(identities.Get(), fleetOptions, policy)));
        })
        .AllowAnonymous()
        .Produces<PairingRedeemResponse>(200)
        .WithName("RedeemPairingCode");

        return app;
    }

    /// <summary>True when the request was made with a machine-kind device token: another machine in the owner's Fleet.</summary>
    private static async Task<bool> IsPeerMachineAsync(HttpContext http, DeviceTokenService devices) =>
        BearerTokenHandler.AuthenticatedWithToken(http.User)
        && FleetClaims.DeviceIdOf(http.User) is { } deviceId
        && (await devices.ListAsync()).Any(d => d.Id == deviceId && d.Kind == DeviceKinds.Machine);

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

}

/// <summary>Which addresses a pairing code carries, in the order a phone should try them, and the link they make.</summary>
public static class PairingAddresses
{
    /// <summary>
    /// Every address, best first: the one someone typed, the saved phone address, then what Fleet listens on (Tailscale,
    /// the home network, the host name). An older phone reads only the first.
    /// </summary>
    public static List<string> For(string? own, string? publicUrl, IEnumerable<string> reachable)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            var normalized = url is null ? null : MachineEndpoints.NormalizeBaseUrl(url.Trim());
            if (normalized is not null && !urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                urls.Add(normalized);
        }

        Add(own);
        Add(publicUrl);
        foreach (var url in reachable)
            Add(url);
        return urls;
    }

    /// <summary>The URL a QR code shows: <c>&lt;first address&gt;/pair#p=&lt;base64url(payload)&gt;</c>.</summary>
    public static string Link(PairingPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, ApiJsonContext.Default.PairingPayload);
        return $"{payload.Url}/pair#p={Base64Url(Encoding.UTF8.GetBytes(json))}";
    }

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
