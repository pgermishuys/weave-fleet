using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>Add a machine → Found: the waiting machines seen on Tailscale and the home network.</summary>
public sealed class FoundMachinesTests
{
    private const string Status = """
        { "Peer": { "nodekey:a": { "HostName": "atlas", "DNSName": "atlas.tail9c2e.ts.net.", "OS": "linux", "Online": true, "TailscaleIPs": ["100.64.90.72"] },
                    "nodekey:b": { "HostName": "vega", "OS": "windows", "Online": true, "TailscaleIPs": ["100.64.90.80"] } } }
        """;

    private static string Waiting(string id, string name, string hint = "7K3M", string expires = "2026-11-19T18:00:00Z") =>
        $$"""{"machineId":"{{id}}","name":"{{name}}","os":"linux","hint":"{{hint}}","expiresAt":"{{expires}}"}""";

    private sealed class FakeTailscale(string? json) : ITailscale
    {
        public Task<string?> StatusJsonAsync(CancellationToken cancellationToken) => Task.FromResult(json);
    }

    private sealed class FakeLan(params LanSighting[] heard) : ILanListener
    {
        public Task<IReadOnlyList<LanSighting>> ListenAsync(TimeSpan window, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LanSighting>>(heard);
    }

    private sealed class Network(Dictionary<string, string> answers) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            lock (Asked)
                Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(answers.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Roster(params string[] ids) : IRemoteMachineRepository
    {
        public Task<IReadOnlyList<RemoteMachine>> ListAsync() =>
            Task.FromResult<IReadOnlyList<RemoteMachine>>(ids.Select(id => new RemoteMachine { Id = id, Name = id, BaseUrl = "http://x", EncryptedToken = "t" }).ToList());
        public Task<RemoteMachine?> GetAsync(string id) => throw new NotSupportedException();
        public Task UpsertAsync(RemoteMachine machine) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id) => throw new NotSupportedException();
        public Task UpdateStatusAsync(string id, string status, DateTimeOffset? lastSeenAt) => throw new NotSupportedException();
        public Task UpdateAddressesAsync(string id, string baseUrl, IReadOnlyList<RemoteMachineAddress> addresses) => throw new NotSupportedException();
        public Task<DeviceGrant?> GetGrantAsync(string deviceId, string machineId) => throw new NotSupportedException();
        public Task SaveGrantAsync(DeviceGrant grant) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceGrant>> ListGrantsForDeviceAsync(string deviceId) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceGrant>> ListGrantsForMachineAsync(string machineId) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceGrant>> ListRevokedGrantsAsync() => throw new NotSupportedException();
        public Task MarkGrantRevokedAsync(string deviceId, string machineId, DateTimeOffset at) => throw new NotSupportedException();
        public Task DeleteGrantAsync(string deviceId, string machineId) => throw new NotSupportedException();
    }

    private static FoundMachines Make(Network network, string? tailscale, ILanListener lan, IRemoteMachineRepository? roster = null, FakeTimeProvider? time = null) =>
        new(new FakeTailscale(tailscale), lan, network,
            new MachineIdentityStore(Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db")), roster ?? new Roster(), new FleetOptions { Port = 6262 },
            time ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T17:50:00Z", System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task A_peer_on_the_tailnet_that_is_waiting_is_found_and_one_that_is_not_is_left_out()
    {
        var network = new Network(new() { ["http://100.64.90.72:6262"] = Waiting("m-atlas", "atlas") });
        var list = await Make(network, Status, new FakeLan()).ListAsync(CancellationToken.None);

        var atlas = list.ShouldHaveSingleItem();
        (atlas.MachineId, atlas.Name, atlas.BaseUrl, atlas.Hint, atlas.Source).ShouldBe(("m-atlas", "atlas", "http://100.64.90.72:6262", "7K3M", FoundSources.Tailscale));
    }

    [Fact]
    public async Task A_machine_heard_on_the_home_network_is_found_only_when_its_address_says_the_same_id()
    {
        var network = new Network(new()
        {
            ["http://192.168.1.40:6262"] = Waiting("m-atlas", "atlas"),
            ["http://192.168.1.41:6262"] = Waiting("m-someone-else", "mallory"),
        });
        var heard = new FakeLan(
            new LanSighting(IPAddress.Parse("192.168.1.40"), new LanAnnouncement(1, "m-atlas", "atlas", "linux", 6262, "7K3M")),
            new LanSighting(IPAddress.Parse("192.168.1.41"), new LanAnnouncement(1, "m-atlas", "atlas", "linux", 6262, "7K3M")));

        var list = await Make(network, null, heard).ListAsync(CancellationToken.None);

        list.Select(f => (f.MachineId, f.BaseUrl, f.Source)).ShouldBe([("m-atlas", "http://192.168.1.40:6262", FoundSources.HomeNetwork)]);
    }

    [Fact]
    public async Task An_announcement_from_a_public_address_is_never_asked()
    {
        var network = new Network(new() { ["http://203.0.113.9:6262"] = Waiting("m-atlas", "atlas") });
        var heard = new FakeLan(new LanSighting(IPAddress.Parse("203.0.113.9"), new LanAnnouncement(1, "m-atlas", "atlas", "linux", 6262, "7K3M")));

        (await Make(network, null, heard).ListAsync(CancellationToken.None)).ShouldBeEmpty();
        network.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_machine_seen_both_ways_is_listed_once_with_the_Tailscale_address()
    {
        var network = new Network(new()
        {
            ["http://100.64.90.72:6262"] = Waiting("m-atlas", "atlas"),
            ["http://192.168.1.40:6262"] = Waiting("m-atlas", "atlas"),
        });
        var heard = new FakeLan(new LanSighting(IPAddress.Parse("192.168.1.40"), new LanAnnouncement(1, "m-atlas", "atlas", "linux", 6262, "7K3M")));

        var list = await Make(network, Status, heard).ListAsync(CancellationToken.None);

        list.ShouldHaveSingleItem().Source.ShouldBe(FoundSources.Tailscale);
    }

    [Fact]
    public async Task A_machine_this_Fleet_already_has_is_left_out()
    {
        var network = new Network(new() { ["http://100.64.90.72:6262"] = Waiting("m-atlas", "atlas") });

        (await Make(network, Status, new FakeLan(), new Roster("m-atlas")).ListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_that_has_run_out_is_left_out_and_looks_within_a_few_seconds_share_an_answer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T17:50:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var network = new Network(new() { ["http://100.64.90.72:6262"] = Waiting("m-atlas", "atlas", expires: "2026-11-19T17:51:00Z") });
        var found = Make(network, Status, new FakeLan(), time: time);

        (await found.ListAsync(CancellationToken.None)).Count.ShouldBe(1);
        var asked = network.Asked.Count;
        (await found.ListAsync(CancellationToken.None)).Count.ShouldBe(1);
        network.Asked.Count.ShouldBe(asked, "the second look used the first answer");

        time.Advance(TimeSpan.FromMinutes(2));
        (await found.ListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public void Only_addresses_on_this_computer_the_home_network_or_Tailscale_are_asked()
    {
        FoundMachines.IsLocalAddress(IPAddress.Parse("192.168.1.40")).ShouldBeTrue();
        FoundMachines.IsLocalAddress(IPAddress.Parse("100.64.90.72")).ShouldBeTrue();
        FoundMachines.IsLocalAddress(IPAddress.Parse("203.0.113.9")).ShouldBeFalse();
    }
}
