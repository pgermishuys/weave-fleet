using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Machines;
using WeaveFleet.Infrastructure.Machines;

namespace WeaveFleet.Infrastructure.Tests.Machines;

/// <summary>What a waiting machine broadcasts on the home network, and what a looking Fleet makes of it.</summary>
public sealed class LanAnnouncementTests
{
    private static readonly DateTimeOffset Expires = DateTimeOffset.Parse("2026-11-19T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void An_announcement_names_the_machine_and_its_port_and_the_start_of_the_code_and_survives_the_wire()
    {
        var bytes = LanAnnouncementWire.Encode(new WaitingAnnouncement("m-atlas", "atlas", "linux", "7K3M", Expires), 6262);

        var heard = LanAnnouncementWire.Decode(bytes).ShouldNotBeNull();
        (heard.MachineId, heard.Name, heard.Os, heard.Port, heard.Hint).ShouldBe(("m-atlas", "atlas", "linux", 6262, "7K3M"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("""{"app":"other","v":1,"id":"x","name":"y","port":1,"hint":"ABCD"}""")]
    [InlineData("""{"app":"weave-fleet","v":2,"id":"x","name":"y","port":1,"hint":"ABCD"}""")]
    [InlineData("""{"app":"weave-fleet","v":1,"name":"y","port":1,"hint":"ABCD"}""")]
    public void Anything_else_on_the_port_is_ignored(string text) =>
        LanAnnouncementWire.Decode(Encoding.UTF8.GetBytes(text)).ShouldBeNull();

    [Fact]
    public void A_datagram_too_big_to_be_one_is_ignored() =>
        LanAnnouncementWire.Decode(new byte[4000]).ShouldBeNull();

    [Fact]
    public async Task A_listener_hears_a_datagram_sent_to_its_port_and_skips_other_traffic()
    {
        int port;
        using (var free = new UdpClient(0))
            port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        var listener = new UdpLanListener(new LanAnnouncementOptions(port), NullLogger<UdpLanListener>.Instance);

        var listening = listener.ListenAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        await Task.Delay(300);
        using var sender = new UdpClient();
        var datagram = LanAnnouncementWire.Encode(new WaitingAnnouncement("m-atlas", "atlas", "linux", "7K3M", Expires), 6262);
        await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port));
        await sender.SendAsync(Encoding.UTF8.GetBytes("not an announcement"), new IPEndPoint(IPAddress.Loopback, port));

        var heard = await listening;

        heard.Select(s => s.Announcement.MachineId).ShouldBe(["m-atlas"]);
        IPAddress.IsLoopback(heard.Single().From).ShouldBeTrue();
    }
}
