using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api.Auth;

/// <summary>An address on one of this machine's interfaces that Fleet could listen on, with what kind of network it is on.</summary>
/// <param name="Address">The interface address.</param>
/// <param name="Kind"><c>tailnet</c> for Tailscale's range, <c>lan</c> for a private home or office address.</param>
public sealed record FoundAddress(IPAddress Address, string Kind);

/// <summary>
/// The extra listeners the desktop app opens when someone lets others reach it. Fleet binds the loopback listener the
/// window uses; this adds listeners on Tailscale and/or the home network (or one more loopback port for a tunnel the
/// person runs). Every one of them always asks for a key: <see cref="LoopbackAuthPolicy"/> grants the no-key sign-in
/// to the loopback listener only, by the endpoint a connection arrived on.
/// <para>
/// Kestrel reads its endpoints from <see cref="Source"/>, and <see cref="ApplyAsync"/> swaps them live, so turning it on
/// doesn't restart Fleet and stop the sessions working in it.
/// </para>
/// </summary>
public sealed class ReachListeners : IDisposable
{
    private readonly FleetOptions _options;
    private readonly ReachStore _store;
    private readonly Func<IReadOnlyList<FoundAddress>> _discover;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _current;

    public ReachListeners(FleetOptions options, ReachStore store, Func<IReadOnlyList<FoundAddress>>? discover = null)
    {
        _options = options;
        _store = store;
        _discover = discover ?? (() => Discover(options.PrivateNetworksOnly));
        _current = Plan(_store.Get());
        Source = new ReachConfigurationSource(Endpoints(_current));
    }

    /// <summary>The configuration section holding Kestrel's endpoints.</summary>
    public const string Section = "Fleet:ReachKestrel";

    /// <summary>Kestrel's endpoints, as configuration it reloads when they change.</summary>
    public IConfigurationSource Source { get; }

    /// <summary>What was chosen, and is open now.</summary>
    public ReachSettings Settings => _current.Settings;

    /// <summary>The network addresses Fleet listens on beyond loopback, with the Fleet port.</summary>
    public IReadOnlyList<FoundAddress> Listening => _current.Network;

    /// <summary>The loopback port that always asks for a key, when the choice is "my own address".</summary>
    public int? OwnPort => _current.OwnPort;

    /// <summary>True when any listener besides the window's loopback one is open.</summary>
    public bool HasListeners => _current.Network.Count > 0 || _current.OwnPort is not null;

    public void Dispose() => _gate.Dispose();

    /// <summary>The addresses each choice would listen on right now, for the dialog.</summary>
    public IReadOnlyList<FoundAddress> Found() => _discover();

    /// <summary>
    /// Opens the listeners for <paramref name="settings"/> and saves the choice. Returns an error sentence when it
    /// can't: nothing found to listen on, or an address that wouldn't open. The previous choice stays in force then.
    /// </summary>
    public async Task<string?> ApplyAsync(ReachSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = _current;
            var planned = Plan(settings with { OwnPort = settings.Mode == ReachModes.Own ? settings.OwnPort ?? previous.OwnPort : null });
            if (settings.Mode is ReachModes.TailscaleAndHome or ReachModes.Home && planned.Network.Count == 0)
            {
                return settings.Mode == ReachModes.Home
                    ? "Fleet didn't find a home network address on this machine. Connect to your Wi-Fi or cable and try again."
                    : "Fleet didn't find a Tailscale or home network address on this machine. Connect to your network and try again.";
            }

            Swap(planned);
            var failed = await FirstClosedAsync(planned, cancellationToken);
            if (failed is not null)
            {
                Swap(previous);
                return $"Fleet couldn't listen on {failed}. Something else may be using the port.";
            }

            _store.Save(planned.Settings);
            _current = planned;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Swap(Snapshot snapshot)
    {
        // Open the new listeners before the policy sees them, close the old ones after: see Endpoints().
        ((ReachConfigurationSource)Source).Provider.Replace(Endpoints(snapshot));
    }

    private Snapshot Plan(ReachSettings settings)
    {
        var wanted = settings.Mode switch
        {
            ReachModes.TailscaleAndHome => _discover(),
            ReachModes.Home => _discover().Where(found => found.Kind == "lan").ToList(),
            _ => [],
        };
        var ownPort = settings.Mode == ReachModes.Own ? settings.OwnPort ?? _options.Port + 1 : (int?)null;
        return new Snapshot(settings with { OwnPort = ownPort }, wanted, ownPort);
    }

    private Dictionary<string, string?> Endpoints(Snapshot snapshot)
    {
        var endpoints = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // The window's listener: loopback only. This is the one that signs in without a key.
            [$"{Section}:Endpoints:loopback:Url"] = $"http://{LoopbackHost(_options.Host)}:{_options.Port}",
        };
        for (var i = 0; i < snapshot.Network.Count; i++)
            endpoints[$"{Section}:Endpoints:reach{i}:Url"] = $"http://{snapshot.Network[i].Address}:{_options.Port}";
        if (snapshot.OwnPort is { } ownPort)
            endpoints[$"{Section}:Endpoints:own:Url"] = $"http://127.0.0.1:{ownPort}";
        return endpoints;
    }

    private static string LoopbackHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        return trimmed is "" or "localhost" ? "127.0.0.1" : trimmed;
    }

    /// <summary>Kestrel rebinds on its own thread after a reload, so connect to each new endpoint to know it's open.</summary>
    private async Task<string?> FirstClosedAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        var targets = snapshot.Network.Select(found => new IPEndPoint(found.Address, _options.Port)).ToList();
        if (snapshot.OwnPort is { } ownPort)
            targets.Add(new IPEndPoint(IPAddress.Loopback, ownPort));

        foreach (var target in targets)
        {
            var open = false;
            for (var attempt = 0; attempt < 40 && !open; attempt++)
            {
                try
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    await client.ConnectAsync(target, timeout.Token);
                    open = true;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            if (!open)
                return target.ToString();
        }

        return null;
    }

    /// <summary>
    /// This machine's Tailscale and private-network IPv4 addresses. Never a public one, and never a container
    /// bridge, whatever the choice: the listener is for devices on your networks.
    /// </summary>
    public static IReadOnlyList<FoundAddress> Discover(bool privateNetworksOnly)
    {
        var found = new List<FoundAddress>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || IsContainerBridge(networkInterface.Name))
                    continue;

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;

                    var kind = MachineAddresses.IsTailnet(address) ? "tailnet" : MachineAddresses.IsPrivate(address) && !IsLinkLocal(address) ? "lan" : null;
                    if (kind is not null && found.All(existing => !existing.Address.Equals(address)))
                        found.Add(new FoundAddress(address, kind));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Some sandboxes hide the interfaces; there is nothing to offer then.
        }

        return found.OrderBy(address => address.Kind == "tailnet" ? 0 : 1).ToList();
    }

    private static bool IsContainerBridge(string name)
        => name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal)
            || name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal)
            || name.StartsWith("cni", StringComparison.Ordinal) || name.StartsWith("podman", StringComparison.Ordinal);

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private sealed record Snapshot(ReachSettings Settings, IReadOnlyList<FoundAddress> Network, int? OwnPort);

    private sealed class ReachConfigurationSource(Dictionary<string, string?> initial) : IConfigurationSource
    {
        public ReachConfigurationProvider Provider { get; } = new(initial);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }

    private sealed class ReachConfigurationProvider : ConfigurationProvider
    {
        public ReachConfigurationProvider(Dictionary<string, string?> initial) => Data = initial;

        public void Replace(Dictionary<string, string?> endpoints)
        {
            Data = endpoints;
            OnReload();
        }
    }
}
