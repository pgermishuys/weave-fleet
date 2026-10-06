using System.Security.Claims;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// What a signed-in caller may do on this machine. The machine token, loopback and the owner's cookie make an
/// <see cref="Owner"/>; a device token (a paired phone) makes a <see cref="Device"/>, which can do everything except
/// manage access: read or replace the machine token, pair or remove devices, or change the machine list.
/// <para>
/// A principal without <see cref="Scope"/> is an owner: cookies signed in before devices existed carry none, and
/// neither does loopback. An agent (<c>amr=agent</c>) is never an owner, whatever it carries.
/// </para>
/// </summary>
public static class FleetClaims
{
    public const string Scope = "fleet_scope";
    public const string Owner = "owner";
    public const string Device = "device";

    /// <summary>The paired device's id, on a <see cref="Device"/> principal.</summary>
    public const string DeviceId = "fleet_device";

    /// <summary>The authorization policy for owner-only endpoints.</summary>
    public const string MachineOwnerPolicy = "MachineOwner";

    public static bool IsOwner(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;
        if (user.HasClaim(BearerTokenHandler.MethodClaim, BearerTokenHandler.AgentMethod))
            return false;
        if (user.HasClaim(c => c.Type == DeviceId))
            return false;

        var scope = user.FindFirst(Scope)?.Value;
        return scope is null or Owner;
    }

    /// <summary>The paired device behind the request, or null when it isn't one.</summary>
    public static string? DeviceIdOf(ClaimsPrincipal user) => user.FindFirst(DeviceId)?.Value;

    /// <summary>The claims a device's principal carries, beside the shared local-user ones.</summary>
    public static IEnumerable<Claim> ForDevice(string deviceId) =>
    [
        new Claim(Scope, Device),
        new Claim(DeviceId, deviceId),
    ];
}
