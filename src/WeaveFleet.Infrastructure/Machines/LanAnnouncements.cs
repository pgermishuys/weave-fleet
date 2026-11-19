using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// Where waiting machines announce themselves on the home network. A plain UDP broadcast on one port rather than
/// mDNS/DNS-SD, because it needs nothing installed and no admin rights on Windows, macOS or Linux: mDNS wants port 5353,
/// which the system's own responder already holds on Windows and macOS, and a Fleet that shares it has to speak the whole
/// protocol. A broadcast datagram is a few lines and is only ever sent while <see cref="WaitingMachine"/> waits, and only heard
/// while Add a machine → Found is open. The port is <c>FLEET_FOUND_PORT</c> (default 6263), so a scratch Fleet on a test port
/// can't be heard by the real one.
/// </summary>
public sealed record LanAnnouncementOptions(int Port)
{
    public const int DefaultPort = 6263;

    public static LanAnnouncementOptions FromEnvironment() =>
        new(int.TryParse(Environment.GetEnvironmentVariable("FLEET_FOUND_PORT"), out var port) && port is > 0 and < 65536 ? port : DefaultPort);
}

public static class LanAnnouncementWire
{
    public static byte[] Encode(WaitingAnnouncement waiting, int port)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("app", "weave-fleet");
            writer.WriteString("id", waiting.MachineId);
            writer.WriteString("name", waiting.Name);
            if (waiting.Os is not null)
                writer.WriteString("os", waiting.Os);
            writer.WriteNumber("port", port);
            writer.WriteString("hint", waiting.Hint);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The announcement in a datagram; null for anything else that arrives on the port.</summary>
    public static LanAnnouncement? Decode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is 0 or > 1024)
            return null;
        try
        {
            using var document = JsonDocument.Parse(datagram.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "app") != "weave-fleet"
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !root.TryGetProperty("port", out var port) || !port.TryGetInt32(out var portNumber))
                return null;
            var id = Text(root, "id");
            var name = Text(root, "name");
            var hint = Text(root, "hint");
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hint)
                ? null
                : new LanAnnouncement(1, id, name, Text(root, "os"), portNumber, hint);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Hears waiting machines for a short while. Opens its socket for that while only, so a Fleet nobody is adding a machine from holds no port.</summary>
public sealed partial class UdpLanListener(LanAnnouncementOptions options, ILogger<UdpLanListener> logger) : ILanListener
{
    public async Task<IReadOnlyList<LanSighting>> ListenAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var heard = new List<LanSighting>();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // A second Fleet on this computer listens too (two scratch Fleets, or one in a container).
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(window);
            var buffer = new byte[1024];
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!timeout.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, timeout.Token);
                if (LanAnnouncementWire.Decode(buffer.AsSpan(0, received.ReceivedBytes)) is { } announcement
                    && received.RemoteEndPoint is IPEndPoint remote)
                    heard.Add(new LanSighting(remote.Address, announcement));
            }
        }
        catch (OperationCanceledException)
        {
            // The window ended (or the request was cancelled): what was heard is the answer.
        }
        catch (SocketException ex)
        {
            LogCouldNotListen(logger, options.Port, ex);
        }

        return heard;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't listen for waiting machines on UDP port {Port}")]
    private static partial void LogCouldNotListen(ILogger logger, int port, Exception exception);
}

/// <summary>While this machine waits (<see cref="WaitingMachine"/>), says so once a second to the home network. Local mode only.</summary>
public sealed partial class LanAnnouncer(
    WaitingMachine waiting,
    LanAnnouncementOptions options,
    FleetOptions fleetOptions,
    ILogger<LanAnnouncer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (fleetOptions.Auth.Enabled || !fleetOptions.Auth.TokenAuthEnabled)
            return;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                if (waiting.Current() is not { } current)
                    continue;
                var datagram = LanAnnouncementWire.Encode(current, fleetOptions.Port);
                foreach (var target in Targets(options.Port))
                {
                    try
                    {
                        await socket.SendToAsync(datagram, SocketFlags.None, target, stoppingToken);
                    }
                    catch (SocketException)
                    {
                        // An interface that is down, or one that can't broadcast: the others still carry it.
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>The broadcast address of every network this machine is on, the general broadcast, and this machine itself (a Fleet beside this one).</summary>
    internal static IReadOnlyList<IPEndPoint> Targets(int port)
    {
        var addresses = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var directed = new byte[4];
                for (var i = 0; i < 4; i++)
                    directed[i] = (byte)(ip[i] | ~mask[i]);
                var broadcast = new IPAddress(directed);
                if (!addresses.Contains(broadcast))
                    addresses.Add(broadcast);
            }
        }

        return addresses.Select(a => new IPEndPoint(a, port)).ToList();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Announcing this machine to the home network failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
