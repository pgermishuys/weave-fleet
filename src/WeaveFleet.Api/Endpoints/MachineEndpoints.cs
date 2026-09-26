using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// This Fleet as a machine other devices can add: who it is, what to call it, and how to reach it.
/// <para>
/// This is the contract every client builds on, the web app today and native apps later: <c>GET /api/machine</c>
/// identifies the Fleet a URL points at, <c>apiVersion</c> says which contract it speaks, and a client presents
/// the access token as <c>Authorization: Bearer</c> (or <c>access_token</c> on a socket). See docs/machines.md.
/// </para>
/// </summary>
public static class MachineEndpoints
{
    /// <summary>
    /// The version of the machine contract. Bump it when a client talking to another machine would break: a
    /// removed field, a changed meaning, a new requirement. Adding a field doesn't need a bump.
    /// </summary>
    public const int ApiVersion = 1;

    private const int MaxNameLength = 64;

    public static IEndpointRouteBuilder MapMachineEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        var group = app.MapGroup("/api/machine").WithTags("Machine");

        group.MapGet("", (MachineIdentityStore store, LoopbackAuthPolicy policy) =>
            Results.Ok(ToResponse(store.Get(), fleetOptions, policy)))
            .Produces<MachineResponse>(200)
            .WithName("GetMachine");

        group.MapPut("", (UpdateMachineRequest request, MachineIdentityStore store, LoopbackAuthPolicy policy) =>
        {
            var name = request.Name?.Trim();
            if (name is { Length: > MaxNameLength })
                return Results.BadRequest(new ErrorResponse($"A machine name can be at most {MaxNameLength} characters."));

            var identity = store.Update(current => current with { Name = string.IsNullOrEmpty(name) ? null : name });
            return Results.Ok(ToResponse(identity, fleetOptions, policy));
        })
        .Produces<MachineResponse>(200)
        .WithName("UpdateMachine");

        // Only local mode has an access token; a Fleet with sign-in identifies people, not devices.
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return app;

        group.MapGet("/access", (ILocalTokenAuthService tokens, LoopbackAuthPolicy policy) =>
            Results.Ok(ToAccessResponse(tokens, fleetOptions, policy)))
            .Produces<MachineAccessResponse>(200)
            .WithName("GetMachineAccess");

        group.MapPost("/access/token", (ILocalTokenAuthService tokens, LoopbackAuthPolicy policy) =>
        {
            if (!tokens.TryReplaceToken(out _))
                return Results.Conflict(new ErrorResponse(
                    "WEAVE_FLEET_AUTH_TOKEN sets this machine's token. Change the variable and restart Fleet to replace it."));

            return Results.Ok(ToAccessResponse(tokens, fleetOptions, policy));
        })
        .Produces<MachineAccessResponse>(200)
        .WithName("ReplaceMachineToken");

        return app;
    }

    private static MachineResponse ToResponse(MachineIdentity identity, FleetOptions options, LoopbackAuthPolicy policy)
    {
        var hostName = Environment.MachineName;
        return new MachineResponse(
            identity.Id,
            identity.Name ?? hostName,
            hostName,
            OperatingSystemName(),
            FleetInstrumentation.ServiceVersion.Split('+')[0],
            ApiVersion,
            options.Auth.Enabled ? "sign-in" : "token",
            policy.IsRemoteReachable,
            !policy.AllowsLoopbackAutoAuth);
    }

    private static MachineAccessResponse ToAccessResponse(ILocalTokenAuthService tokens, FleetOptions options, LoopbackAuthPolicy policy)
        => new(
            tokens.Token,
            tokens.Source switch
            {
                LocalTokenSource.Environment => "environment",
                LocalTokenSource.Saved => "saved",
                _ => "ephemeral",
            },
            options.Host,
            options.Port,
            policy.IsRemoteReachable,
            !policy.AllowsLoopbackAutoAuth,
            policy.IsRemoteReachable ? ReachableAddresses(options) : []);

    /// <summary>Addresses another device could use, when Fleet listens beyond loopback. Tailnet first: it's the one that travels.</summary>
    private static List<MachineAddress> ReachableAddresses(FleetOptions options)
    {
        var addresses = new List<MachineAddress>();

        if (IPAddress.TryParse(options.Host.Trim('[', ']'), out var bound) && !IsWildcard(bound))
        {
            addresses.Add(new MachineAddress(UrlFor(bound, options.Port), KindOf(bound)));
            return addresses;
        }

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;
                    if (IsLinkLocal(address))
                        continue;

                    addresses.Add(new MachineAddress(UrlFor(address, options.Port), KindOf(address)));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Some sandboxes hide the interfaces; the host name below still helps.
        }

        addresses.Add(new MachineAddress($"http://{Environment.MachineName.ToLowerInvariant()}:{options.Port}", "hostname"));

        return addresses
            .DistinctBy(address => address.Url)
            .OrderBy(address => address.Kind switch { "tailnet" => 0, "lan" => 1, _ => 2 })
            .ToList();
    }

    private static bool IsWildcard(IPAddress address)
        => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Tailscale hands out addresses from the carrier-grade NAT range, 100.64.0.0/10.</summary>
    private static string KindOf(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                return "tailnet";
        }

        return "lan";
    }

    private static string UrlFor(IPAddress address, int port)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{address}]:{port}" : $"http://{address}:{port}";

    private static string OperatingSystemName()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsMacOS())
            return "macos";
        if (OperatingSystem.IsLinux())
            return "linux";
        return "other";
    }
}

/// <summary>Who a Fleet is. Any authenticated caller may read it.</summary>
/// <param name="Id">Stable id: survives restarts, port changes and renames. Clients key everything about a machine by it.</param>
/// <param name="Name">What to call the machine: the name someone gave it, otherwise the host name.</param>
/// <param name="HostName">The operating system's name for the computer.</param>
/// <param name="Os"><c>linux</c>, <c>macos</c>, <c>windows</c> or <c>other</c>.</param>
/// <param name="Version">Fleet's version.</param>
/// <param name="ApiVersion">The machine contract version; see <see cref="MachineEndpoints.ApiVersion"/>.</param>
/// <param name="AuthMode"><c>token</c> (local mode: present the access token) or <c>sign-in</c> (cloud mode).</param>
/// <param name="RemoteReachable">Whether Fleet listens on an address other devices can reach.</param>
/// <param name="RequiresToken">Whether every request needs the token, this machine's own included.</param>
public sealed record MachineResponse(
    string Id,
    string Name,
    string HostName,
    string Os,
    string Version,
    int ApiVersion,
    string AuthMode,
    bool RemoteReachable,
    bool RequiresToken);

/// <summary>Renames this machine. An empty name goes back to the host name.</summary>
public sealed record UpdateMachineRequest(string? Name);

/// <summary>How another device reaches this Fleet.</summary>
/// <param name="Token">The access token. Whoever has it can do anything this Fleet can.</param>
/// <param name="TokenSource"><c>saved</c> (Fleet keeps it; it can be replaced), <c>environment</c> (WEAVE_FLEET_AUTH_TOKEN) or <c>ephemeral</c>.</param>
/// <param name="Host">The address Fleet binds to.</param>
/// <param name="Port">The port Fleet listens on.</param>
/// <param name="RemoteReachable">Whether other devices can connect at all.</param>
/// <param name="RequiresToken">Whether every request needs the token, this machine's own included.</param>
/// <param name="Addresses">URLs another device might use, best first. Empty when Fleet only listens on loopback.</param>
public sealed record MachineAccessResponse(
    string Token,
    string TokenSource,
    string Host,
    int Port,
    bool RemoteReachable,
    bool RequiresToken,
    IReadOnlyList<MachineAddress> Addresses);

/// <param name="Url">Base URL, e.g. <c>http://100.64.90.72:2113</c>.</param>
/// <param name="Kind"><c>tailnet</c>, <c>lan</c> or <c>hostname</c>.</param>
public sealed record MachineAddress(string Url, string Kind);
#pragma warning restore IL2026
