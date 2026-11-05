using Shouldly;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>The list in Add a machine → Set it up from here: ssh config hosts and online tailnet peers, merged.</summary>
public sealed class MachineCandidatesTests
{
    private const string Status = """
        {
          "Self": { "HostName": "kestrel", "Online": true },
          "Peer": {
            "nodekey:a": { "HostName": "falcon", "DNSName": "falcon.tail9c2e.ts.net.", "OS": "linux", "Online": true, "TailscaleIPs": ["100.64.90.72"] },
            "nodekey:b": { "HostName": "vega", "DNSName": "vega.tail9c2e.ts.net.", "OS": "windows", "Online": true, "TailscaleIPs": ["100.64.90.80"] },
            "nodekey:c": { "HostName": "osprey", "DNSName": "osprey.tail9c2e.ts.net.", "OS": "macOS", "Online": false },
            "nodekey:d": { "HostName": "heron", "DNSName": "heron.tail9c2e.ts.net.", "OS": "macOS", "Online": true, "TailscaleIPs": ["100.64.90.91"] }
          }
        }
        """;

    private sealed class FakeTailscale(string? json) : ITailscale
    {
        public Task<string?> StatusJsonAsync(CancellationToken cancellationToken) => Task.FromResult(json);
    }

    private static MachineCandidates With(string? tailscale, params SshConfigHost[] hosts) => new(new FakeTailscale(tailscale), () => hosts);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public async Task Online_peers_and_config_hosts_are_listed_by_name_with_where_they_came_from()
    {
        var list = await With(Status, new SshConfigHost("orion", "orion.example.com", "pat", null))
            .ListAsync(None, CancellationToken.None);

        list.Select(c => (c.Name, c.Source, c.Os)).ShouldBe([
            ("falcon", "Tailscale", "linux"),
            ("heron", "Tailscale", "macos"),
            ("orion", "ssh config", null),
            ("vega", "Tailscale", "windows"),
        ]);
        list.Single(c => c.Name == "falcon").Destination.ShouldBe("falcon.tail9c2e.ts.net");
    }

    [Fact]
    public async Task A_config_host_that_is_also_a_peer_is_one_row_and_keeps_the_config_name()
    {
        var list = await With(Status, new SshConfigHost("work-falcon", "100.64.90.72", "pat", null)).ListAsync(None, CancellationToken.None);

        list.Select(c => (c.Name, c.Source, c.Os)).ShouldContain(("work-falcon", "ssh config", "linux"));
        list.ShouldNotContain(c => c.Name == "falcon");
    }

    [Fact]
    public async Task Without_tailscale_only_the_config_is_listed()
    {
        var list = await With(null, new SshConfigHost("orion", null, null, null)).ListAsync(None, CancellationToken.None);

        list.Select(c => c.Name).ShouldBe(["orion"]);
        (await With("not json").ListAsync(None, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_machine_that_is_already_added_is_left_out()
    {
        var list = await With(Status).ListAsync(new HashSet<string> { "falcon.tail9c2e.ts.net" }, CancellationToken.None);

        list.Select(c => c.Name).ShouldBe(["heron", "vega"]);
    }
}
