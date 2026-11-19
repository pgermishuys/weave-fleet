using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// "Let others reach this machine", for the desktop app: which extra listeners (Tailscale, the home network, a port for
/// a tunnel) are open next to the loopback one its window uses. Only the app's own Fleet has them; for any other Fleet
/// <c>available</c> is false and nothing can be changed. See <see cref="ReachListeners"/>.
/// </summary>
public static class ReachEndpoints
{
    public static IEndpointRouteBuilder MapReachEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        var group = app.MapGroup("/api/machine/reach").WithTags("Machine");

        group.MapGet("", async (IServiceProvider services, TailnetName tailnet, ITailscale tailscale, CancellationToken ct) =>
            Results.Ok(await ToResponseAsync(services.GetService<ReachListeners>(), fleetOptions, tailnet, tailscale, ct)))
            .Produces<ReachResponse>(200)
            .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
            .WithName("GetMachineReach");

        group.MapPut("", async (SetReachRequest request, IServiceProvider services, MachineIdentityStore identities, TailnetName tailnet, ITailscale tailscale, CancellationToken ct) =>
        {
            var reach = services.GetService<ReachListeners>();
            if (reach is null)
                return Results.Conflict(new ErrorResponse("Only the desktop app can change who reaches it. A machine started from a terminal uses --host."));

            if (!ReachModes.IsKnown(request.Mode))
                return Results.BadRequest(new ErrorResponse("mode must be off, tailscale-and-home, home or own."));

            string? ownAddress = null;
            if (request.Mode == ReachModes.Own)
            {
                ownAddress = MachineEndpoints.NormalizeBaseUrl(request.OwnAddress?.Trim() ?? string.Empty);
                if (ownAddress is null)
                    return Results.BadRequest(new ErrorResponse("The address must be a full http:// or https:// address, like https://fleet.example.com."));
            }

            var error = await reach.ApplyAsync(new ReachSettings(request.Mode, ownAddress), ct);
            if (error is not null)
                return Results.Conflict(new ErrorResponse(error));

            // Pairing codes point phones at the address the person gave.
            if (request.Mode == ReachModes.Own)
                identities.Update(current => current with { PublicUrl = ownAddress });

            return Results.Ok(await ToResponseAsync(reach, fleetOptions, tailnet, tailscale, ct));
        })
        .Produces<ReachResponse>(200)
        .RequireAuthorization(FleetClaims.MachineOwnerPolicy)
        .WithName("SetMachineReach");

        return app;
    }

    private static async Task<ReachResponse> ToResponseAsync(ReachListeners? reach, FleetOptions options, TailnetName tailnet, ITailscale tailscale, CancellationToken ct)
    {
        if (reach is null)
            return new ReachResponse(false, ReachModes.Off, null, options.Port + 1, [], [], null);

        var name = await tailnet.GetAsync(ct);
        var covered = reach.Listening.Any(address => address.Kind == "tailnet");
        var check = TailscaleChecks.Read(await tailscale.StatusJsonAsync(ct), "this machine", covered);
        return new ReachResponse(
            true,
            reach.Settings.Mode,
            reach.Settings.OwnAddress,
            reach.OwnPort ?? options.Port + 1,
            reach.HasListeners ? MachineEndpoints.ReachableAddresses(options, name, reach) : [],
            Found(reach, options, name),
            check);
    }

    /// <summary>What the choices would listen on: the tailnet name and IP, then each home network address.</summary>
    private static List<MachineAddress> Found(ReachListeners reach, FleetOptions options, string? tailnetName)
    {
        var found = reach.Found()
            .Select(address => new MachineAddress($"http://{address.Address}:{options.Port}", address.Kind))
            .ToList();
        if (tailnetName is not null && found.Any(address => address.Kind == "tailnet"))
            found.Insert(0, new MachineAddress($"http://{tailnetName}:{options.Port}", "tailnet"));
        return found;
    }
}

/// <summary>Where the desktop app can be reached, and what Fleet found to offer.</summary>
/// <param name="Available">False for a Fleet that isn't the desktop app's own: nothing here can be changed.</param>
/// <param name="Mode">One of <c>off</c>, <c>tailscale-and-home</c>, <c>home</c>, <c>own</c>.</param>
/// <param name="OwnAddress">For <c>own</c>: the address the person gave.</param>
/// <param name="OwnPort">The loopback port that always asks for a key, for a tunnel or proxy to send to when the mode is <c>own</c>.</param>
/// <param name="Listening">Where other devices can reach it now, best first. Empty when off.</param>
/// <param name="Tailscale">Whether Tailscale is installed, running and signed in here, and whether the listener covers it. Null when not available.</param>
/// <param name="Found">The addresses on this machine's networks, <c>tailnet</c> first then <c>lan</c>: what the choices would use.</param>
public sealed record ReachResponse(bool Available, string Mode, string? OwnAddress, int OwnPort, IReadOnlyList<MachineAddress> Listening, IReadOnlyList<MachineAddress> Found, TailscaleCheck? Tailscale);

/// <summary>Sets where the desktop app can be reached.</summary>
/// <param name="Mode">One of <c>off</c>, <c>tailscale-and-home</c>, <c>home</c>, <c>own</c>.</param>
/// <param name="OwnAddress">Required for <c>own</c>.</param>
public sealed record SetReachRequest(string Mode, string? OwnAddress = null);
