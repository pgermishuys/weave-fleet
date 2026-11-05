using System.Text.Json;

namespace WeaveFleet.Application.Machines;

/// <summary>A machine Fleet can already see: <see cref="Destination"/> is what to log in to. <see cref="Os"/> is only known from Tailscale.</summary>
public sealed record MachineCandidate(string Name, string Destination, string Source, string? Os);

public static class MachineCandidateSources
{
    public const string Tailscale = "Tailscale";
    public const string SshConfig = "ssh config";
}

/// <summary>Asks Tailscale who is on the tailnet: the JSON of <c>tailscale status --json</c>, or null when it isn't installed or not running.</summary>
public interface ITailscale
{
    Task<string?> StatusJsonAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The machines Add a machine → Set it up from here lists: hosts in <c>~/.ssh/config</c> and online peers on the tailnet
/// (only when <c>tailscale</c> is installed). Nothing is scanned and nothing is logged in to here; a row is looked at
/// when it shows (<see cref="SshMachineService.ProbeAsync"/>).
/// </summary>
public sealed class MachineCandidates(ITailscale tailscale, Func<IReadOnlyList<SshConfigHost>>? configHosts = null)
{
    private readonly Func<IReadOnlyList<SshConfigHost>> _configHosts = configHosts ?? SshConfigHosts.ReadDefault;

    /// <param name="alreadyAdded">Destinations of machines this Fleet already has; they are left out.</param>
    public async Task<IReadOnlyList<MachineCandidate>> ListAsync(IReadOnlySet<string> alreadyAdded, CancellationToken cancellationToken)
    {
        var peers = ParsePeers(await tailscale.StatusJsonAsync(cancellationToken));
        var merged = new List<MachineCandidate>();
        var usedPeers = new HashSet<TailscalePeer>();
        foreach (var host in _configHosts())
        {
            var peer = peers.FirstOrDefault(p => p.Names.Contains(host.Alias, StringComparer.OrdinalIgnoreCase)
                || (host.HostName is not null && p.Names.Contains(host.HostName, StringComparer.OrdinalIgnoreCase)));
            if (peer is not null)
                usedPeers.Add(peer);
            merged.Add(new MachineCandidate(host.Alias, host.Alias, MachineCandidateSources.SshConfig, peer?.Os));
        }

        foreach (var peer in peers.Where(p => !usedPeers.Contains(p)))
            merged.Add(new MachineCandidate(peer.HostName, peer.DnsName ?? peer.HostName, MachineCandidateSources.Tailscale, peer.Os));

        return merged
            .Where(c => SshDestination.Parse(c.Destination) is not null && !alreadyAdded.Contains(c.Destination))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record TailscalePeer(string HostName, string? DnsName, string? Os, IReadOnlyList<string> Names);

    private static List<TailscalePeer> ParsePeers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
                return [];
            var list = new List<TailscalePeer>();
            foreach (var entry in peers.EnumerateObject())
            {
                var peer = entry.Value;
                if (!peer.TryGetProperty("Online", out var online) || online.ValueKind != JsonValueKind.True)
                    continue;
                var hostName = Text(peer, "HostName");
                var dns = Text(peer, "DNSName")?.TrimEnd('.');
                if (string.IsNullOrEmpty(hostName))
                    continue;
                var names = new List<string> { hostName };
                if (dns is { Length: > 0 })
                    names.Add(dns);
                if (peer.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                    names.AddRange(ips.EnumerateArray().Select(ip => ip.GetString()).OfType<string>());
                list.Add(new TailscalePeer(hostName, string.IsNullOrEmpty(dns) ? null : dns, Text(peer, "OS")?.ToLowerInvariant() switch { "macos" or "darwin" => "macos", { } os => os, null => null }, names));
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
