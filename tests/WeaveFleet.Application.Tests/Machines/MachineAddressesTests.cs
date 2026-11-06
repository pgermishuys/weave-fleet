using System.Net;
using Shouldly;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>A machine's addresses: what kind each is, the order they're tried in, and how reported ones are learned.</summary>
public sealed class MachineAddressesTests
{
    private const string Name = "http://studio.tail4f2a.ts.net:2113";
    private const string TailIp = "http://100.88.14.3:2113";
    private const string Lan = "http://192.168.1.42:2113";
    private const string Host = "http://studio:2113";

    [Theory]
    [InlineData(Name, MachineAddresses.Tailnet)]
    [InlineData(TailIp, MachineAddresses.TailnetIp)]
    [InlineData(Lan, MachineAddresses.Lan)]
    [InlineData("http://10.0.0.5:2113", MachineAddresses.Lan)]
    [InlineData("http://172.20.1.9:2113", MachineAddresses.Lan)]
    [InlineData("http://[fd12:3456::1]:2113", MachineAddresses.Lan)]
    [InlineData("http://[fd7a:115c:a1e0::1]:2113", MachineAddresses.TailnetIp)]
    [InlineData(Host, MachineAddresses.HostName)]
    [InlineData("http://studio.local:2113", MachineAddresses.HostName)]
    [InlineData("https://fleet.example.com", MachineAddresses.Other)]
    [InlineData("http://203.0.113.9:2113", MachineAddresses.Other)]
    [InlineData("http://127.0.0.1:40123", MachineAddresses.Other)]
    [InlineData("http://localhost:40123", MachineAddresses.Other)]
    public void Each_address_has_a_kind_from_its_host(string url, string kind)
        => MachineAddresses.KindOf(url).ShouldBe(kind);

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:203.0.113.9", false)]
    public void Only_loopback_private_link_local_and_tailscale_addresses_are_private(string address, bool isPrivate)
        => MachineAddresses.IsPrivate(IPAddress.Parse(address)).ShouldBe(isPrivate);

    [Fact]
    public void Reported_addresses_go_tailscale_name_then_ip_then_local_network_then_host_name()
    {
        var list = MachineAddresses.Learn(MachineAddresses.Seed(Lan), [Host, Lan, TailIp, Name], Lan);

        Urls(list).ShouldBe([Name, TailIp, Lan, Host]);
        list.Single(a => a.Url == Lan).Learned.ShouldBeFalse("it's the address the machine was added with");
        list.Where(a => a.Url != Lan).ShouldAllBe(a => a.Learned);
    }

    [Fact]
    public void A_new_address_goes_in_at_its_kinds_place_and_the_owners_order_stays()
    {
        // The owner put the local network first.
        var owners = Learned(Lan, Name, Host);

        var list = MachineAddresses.Learn(owners, [Lan, Name, Host, TailIp], Lan);

        // The Tailscale IP goes after the last address of its own or an earlier kind: below the name, not above Lan.
        Urls(list).ShouldBe([Lan, Name, TailIp, Host]);
    }

    [Fact]
    public void A_new_local_address_takes_the_place_of_the_one_it_replaces()
    {
        var owners = Learned(Lan, Name, TailIp);

        var list = MachineAddresses.Learn(owners, [Name, TailIp, "http://192.168.1.77:2113"], Name);

        Urls(list).ShouldBe(["http://192.168.1.77:2113", Name, TailIp]);
    }

    [Fact]
    public void Learned_addresses_no_longer_reported_drop_away_but_added_ones_and_the_one_in_use_stay()
    {
        var list = new List<RemoteMachineAddress>
        {
            new(Name, MachineAddresses.Tailnet, Learned: false),
            new(TailIp, MachineAddresses.TailnetIp, Learned: true),
            new(Lan, MachineAddresses.Lan, Learned: true),
            new(Host, MachineAddresses.HostName, Learned: true),
        };

        var learned = MachineAddresses.Learn(list, [Host], Lan);

        Urls(learned).ShouldBe([Name, Lan, Host]);
    }

    [Fact]
    public void The_address_in_use_is_always_listed()
    {
        var list = MachineAddresses.Learn([], [Name, TailIp], Lan);

        Urls(list).ShouldBe([Name, TailIp, Lan]);
        list.Single(a => a.Url == Lan).Learned.ShouldBeFalse();
    }

    [Fact]
    public void Reported_addresses_are_normalized_and_counted_once()
    {
        var list = MachineAddresses.Learn(MachineAddresses.Seed(Lan), [Lan + "/", "HTTP://192.168.1.42:2113", "not an address"], Lan);

        Urls(list).ShouldBe([Lan]);
    }

    [Fact]
    public void Include_adds_an_address_once_without_dropping_learned_ones()
    {
        var list = Learned(Name, TailIp);

        Urls(MachineAddresses.Include(list, Lan)).ShouldBe([Name, TailIp, Lan]);
        MachineAddresses.Include(list, TailIp).ShouldBeSameAs(list);
    }

    [Fact]
    public void Reorder_takes_the_owners_order_only_when_it_is_the_same_addresses()
    {
        var list = Learned(Name, TailIp, Lan);

        Urls(MachineAddresses.Reorder(list, [Lan, Name, TailIp])!).ShouldBe([Lan, Name, TailIp]);
        MachineAddresses.Reorder(list, [Lan, Name]).ShouldBeNull();
        MachineAddresses.Reorder(list, [Lan, Lan, Name]).ShouldBeNull();
        MachineAddresses.Reorder(list, [Lan, Name, Host]).ShouldBeNull();
    }

    [Fact]
    public void Record_keeps_what_came_of_a_try()
    {
        var at = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

        var list = MachineAddresses.Record(Learned(Name, Lan), Name, RemoteMachineAddressResults.NoAnswer, at);

        list[0].LastResult.ShouldBe(RemoteMachineAddressResults.NoAnswer);
        list[0].LastTriedAt.ShouldBe(at);
        list[1].LastResult.ShouldBeNull();
    }

    private static List<RemoteMachineAddress> Learned(params string[] urls)
        => urls.Select(url => new RemoteMachineAddress(url, MachineAddresses.KindOf(url), Learned: true)).ToList();

    private static List<string> Urls(IEnumerable<RemoteMachineAddress> list) => list.Select(a => a.Url).ToList();
}
