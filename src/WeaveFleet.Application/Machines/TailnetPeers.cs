using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WeaveFleet.Application.Machines;

/// <summary>An online machine on the tailnet. <see cref="Names"/> are every name it answers to (host name, MagicDNS name, Tailscale IPs).</summary>
public sealed record TailnetPeer(string HostName, string? DnsName, string? Os, IReadOnlyList<string> Names)
{
    /// <summary>Its first Tailscale IPv4 address, the one that needs no name lookup.</summary>
    public string? Ip => Names.FirstOrDefault(n => IPAddress.TryParse(n, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
}

/// <summary>
/// The online peers in <c>tailscale status --json</c>. One reader for everything that looks at the tailnet:
/// <see cref="MachineCandidates"/> (Add a machine → Set it up from here) and <see cref="FoundMachines"/> (the waiting
/// machines Add a machine → Found lists), so the two don't fork.
/// </summary>
public static class TailnetPeers
{
    public static List<TailnetPeer> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
                return [];
            var list = new List<TailnetPeer>();
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
                list.Add(new TailnetPeer(hostName, string.IsNullOrEmpty(dns) ? null : dns, Text(peer, "OS")?.ToLowerInvariant() switch { "macos" or "darwin" => "macos", { } os => os, null => null }, names));
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
