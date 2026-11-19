using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api.Auth;

/// <summary>The addresses this machine reports to other machines (see <c>GET /api/machine</c>); none while it listens on loopback only.</summary>
public sealed class SelfAddresses(FleetOptions options, TailnetName tailnet, LoopbackAuthPolicy policy) : ISelfAddresses
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        policy.IsRemoteReachable
            ? MachineEndpoints.ReachableAddresses(options, await tailnet.GetAsync(cancellationToken), policy.Reach).Select(address => address.Url).ToList()
            : [];
}
