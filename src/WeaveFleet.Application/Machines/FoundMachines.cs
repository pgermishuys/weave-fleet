using System.Net;
using System.Text.Json;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// What a waiting machine says about itself: its id, its name and the first half of its pairing code. Never a secret:
/// the rest of the code is typed from the terminal that printed it (see <see cref="WaitingMachine"/>).
/// </summary>
public sealed record WaitingAnnouncement(string MachineId, string Name, string? Os, string Hint, DateTimeOffset ExpiresAt);

/// <summary>One datagram a waiting machine broadcasts on the home network: who, where (its Fleet's port) and the code hint.</summary>
public sealed record LanAnnouncement(int V, string MachineId, string Name, string? Os, int Port, string Hint);

/// <summary>An announcement heard on the home network, and the address it came from.</summary>
public sealed record LanSighting(IPAddress From, LanAnnouncement Announcement);

/// <summary>Listens for what waiting machines broadcast on the home network.</summary>
public interface ILanListener
{
    Task<IReadOnlyList<LanSighting>> ListenAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>A machine that is waiting to join a Fleet, and where it was seen. <see cref="Source"/> is how Fleet found it.</summary>
public sealed record FoundMachine(string MachineId, string Name, string? Os, string BaseUrl, string Hint, string Source, DateTimeOffset ExpiresAt);

public static class FoundSources
{
    public const string Tailscale = "Tailscale";
    public const string HomeNetwork = "Home network";
}

/// <summary>
/// This machine, while it is waiting for another machine's Fleet to add it: from the moment <c>fleet node pair</c> (or
/// <c>fleet node up</c>, which runs it) makes a pairing code, until the code is used or runs out. While it waits, it
/// announces itself on the home network and answers <c>GET /api/pairing/waiting</c> for machines that look for it over
/// Tailscale. It says its id, its name and the first four characters of the code, and nothing that adds it: the rest of the
/// code is typed from the terminal, so a machine that only listens in learns nothing it can use.
/// </summary>
public sealed class WaitingMachine(MachineIdentityStore identity, TimeProvider time)
{
    private readonly object _gate = new();
    private string? _hint;
    private DateTimeOffset _expiresAt;

    /// <summary>Starts waiting. <paramref name="manualCode"/> is the code as the terminal shows it (<c>XXXX-XXXX</c>); only its first half is kept.</summary>
    public void Open(string manualCode, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            _hint = manualCode.Length >= 4 ? manualCode[..4] : manualCode;
            _expiresAt = expiresAt;
        }
    }

    /// <summary>The code was used (or the machine was added another way): nothing waits any more.</summary>
    public void Close()
    {
        lock (_gate)
            _hint = null;
    }

    public WaitingAnnouncement? Current()
    {
        lock (_gate)
        {
            if (_hint is null || _expiresAt <= time.GetUtcNow())
                return null;
            var self = identity.Get();
            return new WaitingAnnouncement(self.Id, self.Name ?? Environment.MachineName, OperatingSystemName(), _hint, _expiresAt);
        }
    }

    private static string OperatingSystemName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

/// <summary>
/// The waiting machines Add a machine → Found lists. Fleet looks in two places and never scans a network:
/// <list type="bullet">
/// <item><b>Tailscale</b>: it asks Tailscale who is online (the same reader <see cref="MachineCandidates"/> uses) and asks each
/// peer, on Fleet's port, whether it is waiting.</item>
/// <item><b>The home network</b>: it listens for a moment for the datagrams waiting machines broadcast.</item>
/// </list>
/// Whatever is heard is checked by asking that address <c>GET /api/pairing/waiting</c>, and the answer must say the same
/// machine id, so an announcement can't point Fleet at an address that isn't the machine. Looks are shared: one at a time,
/// and a look within <see cref="CacheFor"/> of the last gets its answer.
/// </summary>
public sealed class FoundMachines(
    ITailscale tailscale,
    ILanListener lan,
    IHttpClientFactory httpClients,
    MachineIdentityStore identity,
    IRemoteMachineRepository machines,
    FleetOptions options,
    TimeProvider time) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ListenFor = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private const int MaxPeersAsked = 64;
    private const int DefaultFleetPort = 6262;

    private readonly SemaphoreSlim _looking = new(1, 1);
    private IReadOnlyList<FoundMachine> _last = [];
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    public void Dispose() => _looking.Dispose();

    /// <summary>The machines waiting to join, except this one and the ones this Fleet already has.</summary>
    public async Task<IReadOnlyList<FoundMachine>> ListAsync(CancellationToken cancellationToken)
    {
        await _looking.WaitAsync(cancellationToken);
        try
        {
            if (time.GetUtcNow() - _lastAt >= CacheFor)
            {
                _last = await LookAsync(cancellationToken);
                _lastAt = time.GetUtcNow();
            }

            return await WithoutKnownAsync(_last);
        }
        finally
        {
            _looking.Release();
        }
    }

    private async Task<IReadOnlyList<FoundMachine>> WithoutKnownAsync(IReadOnlyList<FoundMachine> list)
    {
        var known = (await machines.ListAsync()).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        return list.Where(f => !known.Contains(f.MachineId) && f.ExpiresAt > now).ToList();
    }

    private async Task<IReadOnlyList<FoundMachine>> LookAsync(CancellationToken cancellationToken)
    {
        var selfId = identity.Get().Id;
        var tailnet = AskTailnetAsync(cancellationToken);
        var homeNetwork = AskHomeNetworkAsync(cancellationToken);
        var found = new Dictionary<string, FoundMachine>(StringComparer.Ordinal);
        // Tailscale first: it is encrypted, so when both see a machine that is the address to keep.
        foreach (var machine in (await tailnet).Concat(await homeNetwork))
            if (machine.MachineId != selfId)
                found.TryAdd(machine.MachineId, machine);
        return found.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<FoundMachine>> AskTailnetAsync(CancellationToken cancellationToken)
    {
        var peers = TailnetPeers.Parse(await tailscale.StatusJsonAsync(cancellationToken)).Where(p => p.Ip is not null).Take(MaxPeersAsked);
        var ports = new[] { options.Port, DefaultFleetPort }.Where(p => p > 0).Distinct().ToArray();
        var asks = peers.SelectMany(peer => ports.Select(port => ProbeAsync($"http://{peer.Ip}:{port}", FoundSources.Tailscale, null, cancellationToken)));
        return (await Task.WhenAll(asks)).OfType<FoundMachine>().ToList();
    }

    private async Task<IReadOnlyList<FoundMachine>> AskHomeNetworkAsync(CancellationToken cancellationToken)
    {
        var heard = await lan.ListenAsync(ListenFor, cancellationToken);
        var asks = heard
            .Where(s => s.Announcement.Port is > 0 and <= 65535 && IsLocalAddress(s.From))
            .GroupBy(s => (s.From.ToString(), s.Announcement.Port))
            .Select(g => g.First())
            .Take(MaxPeersAsked)
            .Select(s => ProbeAsync($"http://{FormatHost(s.From)}:{s.Announcement.Port}", FoundSources.HomeNetwork, s.Announcement.MachineId, cancellationToken));
        return (await Task.WhenAll(asks)).OfType<FoundMachine>().ToList();
    }

    /// <summary>Only an address on this computer, the home network or Tailscale is ever asked: a datagram can't send Fleet out onto the internet.</summary>
    internal static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || MachineAddresses.IsPrivate(address) || MachineAddresses.IsTailnet(address)
        || (address.IsIPv6LinkLocal);

    private static string FormatHost(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private async Task<FoundMachine?> ProbeAsync(string baseUrl, string source, string? expectedId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).GetAsync($"{baseUrl}/api/pairing/waiting", timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            var id = Text(root, "machineId");
            var name = Text(root, "name");
            var hint = Text(root, "hint");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(hint) || hint.Length > 8
                || (expectedId is not null && id != expectedId)
                || !root.TryGetProperty("expiresAt", out var expires) || !expires.TryGetDateTimeOffset(out var expiresAt))
                return null;
            return new FoundMachine(id, name.Length > 64 ? name[..64] : name, Text(root, "os"), baseUrl, hint.ToUpperInvariant(), source, expiresAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
