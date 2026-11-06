using System.Net;
using System.Net.Sockets;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// The addresses one machine answers on, and the order Fleet tries them in. Tailscale goes first because it is
/// encrypted: its name, then its IP, then the local network (only used when Tailscale doesn't answer), then the
/// host name, then anything else. The owner can drag them into another order; addresses learned later keep that
/// order and go in at their own kind's place. See docs/machines.md.
/// </summary>
public static class MachineAddresses
{
    /// <summary>Its MagicDNS name, e.g. <c>http://studio.tail4f2a.ts.net:2113</c>.</summary>
    public const string Tailnet = "tailnet";

    /// <summary>Its Tailscale IP, from 100.64.0.0/10.</summary>
    public const string TailnetIp = "tailnet-ip";

    /// <summary>A private address on the local network: 10/8, 172.16/12, 192.168/16, or an IPv6 unique-local one.</summary>
    public const string Lan = "lan";

    /// <summary>A bare host name (<c>studio</c>, <c>studio.local</c>) the network resolves.</summary>
    public const string HostName = "hostname";

    /// <summary>Anything else: a public name, a tunnel on this machine.</summary>
    public const string Other = "other";

    /// <summary>What kind of address <paramref name="url"/> is, from its host alone.</summary>
    public static string KindOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Other;

        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsTailnet(ip))
                return TailnetIp;
            return IsPrivate(ip) && !IPAddress.IsLoopback(ip) ? Lan : Other;
        }

        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            return Tailnet;
        if (!host.Contains('.', StringComparison.Ordinal) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return HostName;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
            return HostName;
        return Other;
    }

    /// <summary>Where a kind goes when nobody has said otherwise; lower is tried first.</summary>
    public static int RankOf(string kind) => kind switch
    {
        Tailnet => 0,
        TailnetIp => 1,
        Lan => 2,
        HostName => 3,
        _ => 4,
    };

    /// <summary>Tailscale hands out addresses from the carrier-grade NAT range, 100.64.0.0/10 (and fd7a:115c:a1e0::/48).</summary>
    public static bool IsTailnet(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0;
    }

    /// <summary>
    /// Whether <paramref name="address"/> can only be reached from this machine, its local network or the tailnet:
    /// loopback, the private ranges, link-local, Tailscale's range and IPv6 unique-local. Anything else is public.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || IsTailnet(address);
        }

        // fc00::/7 unique-local (Tailscale's fd7a:… among them) and fe80::/10 link-local.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && ((bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal);
    }

    /// <summary>A new list for a machine first reached at <paramref name="url"/>: that one address, added by hand.</summary>
    public static IReadOnlyList<RemoteMachineAddress> Seed(string url) => [new(url, KindOf(url), Learned: false)];

    /// <summary>
    /// The list after the machine reported <paramref name="reported"/> as its addresses now. Learned addresses it no
    /// longer reports drop away (not the one in use); an address that replaces one of the same kind takes its place, so
    /// a new DHCP lease keeps the owner's order; any other new one goes in at its kind's place. Addresses added by hand
    /// stay.
    /// </summary>
    public static IReadOnlyList<RemoteMachineAddress> Learn(IReadOnlyList<RemoteMachineAddress> current, IEnumerable<string> reported, string inUse)
    {
        // The address in use is always on the list: it answered, whoever added it.
        var list = Include(current, inUse).ToList();
        var fresh = reported
            .Select(url => RemoteMachineService.NormalizeBaseUrl(url))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Slots of learned addresses that went away, by kind, so a replacement can take one.
        var freed = new List<(int Index, string Kind)>();
        for (var i = 0; i < list.Count; i++)
        {
            var address = list[i];
            if (address.Learned && !string.Equals(address.Url, inUse, StringComparison.OrdinalIgnoreCase)
                && !fresh.Contains(address.Url, StringComparer.OrdinalIgnoreCase))
            {
                freed.Add((i, address.Kind));
                list[i] = null!;
            }
        }

        foreach (var url in fresh)
        {
            if (list.Any(address => address is not null && string.Equals(address.Url, url, StringComparison.OrdinalIgnoreCase)))
                continue;

            var learned = new RemoteMachineAddress(url, KindOf(url), Learned: true);
            var slot = freed.FindIndex(f => f.Kind == learned.Kind);
            if (slot >= 0)
            {
                list[freed[slot].Index] = learned;
                freed.RemoveAt(slot);
                continue;
            }

            list = Insert(list, learned);
        }

        return list.Where(address => address is not null).ToList();
    }

    /// <summary>
    /// Puts <paramref name="address"/> after the last address of its own or an earlier kind, so it never jumps above
    /// one the owner moved up; in the default order that's its kind's place.
    /// </summary>
    private static List<RemoteMachineAddress> Insert(List<RemoteMachineAddress> list, RemoteMachineAddress address)
    {
        var rank = RankOf(address.Kind);
        list.Insert(list.FindLastIndex(other => other is not null && RankOf(other.Kind) <= rank) + 1, address);
        return list;
    }

    /// <summary>The list with <paramref name="url"/> on it, added by hand at its kind's place when it wasn't.</summary>
    public static IReadOnlyList<RemoteMachineAddress> Include(IReadOnlyList<RemoteMachineAddress> current, string url)
        => current.Any(address => string.Equals(address.Url, url, StringComparison.OrdinalIgnoreCase))
            ? current
            : Insert(current.ToList(), new RemoteMachineAddress(url, KindOf(url), Learned: false));

    /// <summary>
    /// The list in the owner's order. Null when <paramref name="urls"/> isn't exactly the machine's addresses (one was
    /// learned or dropped since the page read them).
    /// </summary>
    public static IReadOnlyList<RemoteMachineAddress>? Reorder(IReadOnlyList<RemoteMachineAddress> current, IReadOnlyList<string> urls)
    {
        if (urls.Count != current.Count)
            return null;
        var reordered = new List<RemoteMachineAddress>(current.Count);
        foreach (var url in urls)
        {
            var match = current.FirstOrDefault(address => string.Equals(address.Url, url, StringComparison.OrdinalIgnoreCase));
            if (match is null || reordered.Contains(match))
                return null;
            reordered.Add(match);
        }

        return reordered;
    }

    /// <summary>The list with what came of trying <paramref name="url"/> just now.</summary>
    public static IReadOnlyList<RemoteMachineAddress> Record(IReadOnlyList<RemoteMachineAddress> current, string url, string result, DateTimeOffset at)
        => current
            .Select(address => string.Equals(address.Url, url, StringComparison.OrdinalIgnoreCase) ? address with { LastResult = result, LastTriedAt = at } : address)
            .ToList();
}
