using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Your Fleet (Labs): every machine keeps the whole list. Three Fleets (hangar, atlas, workbench) reach each other by
/// name through a router, and each says it can be reached at https://{name}.test.
/// </summary>
[Collection(TwoFleetTestGroup.Name)]
public sealed class FleetRosterTests
{
    [Fact]
    public async Task Adding_atlas_through_hangar_makes_atlas_know_hangar_and_the_link_works_both_ways()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        var onAtlas = await atlas.ListAsync();
        onAtlas.Select(m => m.Id).ShouldBe([hangar.Id]);
        onAtlas.Single().IntroducedBy.ShouldBe(hangar.Id);
        (await hangar.ListAsync()).Single().IntroducedBy.ShouldBeNull("hangar added atlas itself");
        (await atlas.Machines.CheckAsync(hangar.Id, CancellationToken.None)).Problem.ShouldBeNull("atlas reaches hangar with the key hangar made");
        (await hangar.Machines.CheckAsync(atlas.Id, CancellationToken.None)).Problem.ShouldBeNull();
    }

    [Fact]
    public async Task Adding_workbench_through_atlas_gives_all_three_the_whole_list()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await world.SettleAsync();

        await World.AddAsync(atlas, workbench);
        await world.SettleAsync();

        foreach (var machine in new[] { hangar, atlas, workbench })
        {
            var others = new[] { hangar, atlas, workbench }.Where(o => o != machine).Select(o => o.Id).Order();
            (await machine.ListAsync()).Select(m => m.Id).Order().ShouldBe(others, $"{machine.Name}'s list");
            foreach (var other in others)
                (await machine.Machines.CheckAsync(other, CancellationToken.None)).Problem.ShouldBeNull($"{machine.Name} reaches {other}");
        }

        (await hangar.ListAsync()).Single(m => m.Id == workbench.Id).IntroducedBy.ShouldBe(atlas.Id, "atlas introduced workbench to hangar");
    }

    [Fact]
    public async Task A_key_is_made_by_the_machine_it_opens_and_no_owner_token_is_handed_on()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await World.AddAsync(atlas, workbench);
        await world.SettleAsync();

        // Hangar's key for workbench was made on workbench, when atlas asked for it: a machine-kind device, not the owner token.
        var hangarsKeyForWorkbench = hangar.Machines.TokenOf((await hangar.ListAsync()).Single(m => m.Id == workbench.Id));
        hangarsKeyForWorkbench.ShouldStartWith("fdt_");
        hangarsKeyForWorkbench.ShouldNotBe(workbench.OwnerToken);
        var madeOnWorkbench = (await workbench.Devices.ListAsync()).Where(d => d.Kind == DeviceKinds.Machine).ToList();
        madeOnWorkbench.ShouldContain(d => d.PairedVia == atlas.Id && d.Name.Contains("hangar"));

        foreach (var machine in new[] { hangar, atlas, workbench })
            foreach (var entry in (await machine.ListAsync()).Where(m => m.IntroducedBy is not null))
                machine.Machines.TokenOf(entry).ShouldNotBe(world.Machines[entry.Id].OwnerToken, $"{machine.Name} holds a key for {entry.Name}, not its owner token");
    }

    [Fact]
    public async Task A_machine_that_was_away_catches_up_when_it_comes_back()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await world.SettleAsync();

        world.Router.Down.Add("atlas");
        await World.AddAsync(hangar, workbench);
        await world.SettleAsync();
        (await atlas.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id], "atlas hasn't heard");
        (await workbench.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id]);

        world.Router.Down.Remove("atlas");
        await hangar.Roster.SyncWithAsync(atlas.Id, CancellationToken.None);
        await world.SettleAsync();

        (await atlas.ListAsync()).Select(m => m.Id).Order().ShouldBe(new[] { hangar.Id, workbench.Id }.Order());
        (await workbench.ListAsync()).Select(m => m.Id).Order().ShouldBe(new[] { hangar.Id, atlas.Id }.Order());
        (await atlas.Machines.CheckAsync(workbench.Id, CancellationToken.None)).Problem.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_nobody_took_is_removed_again()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        hangar.Addresses.Clear(); // hangar can't be reached at any address atlas is told
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        (await atlas.ListAsync()).ShouldBeEmpty();
        (await hangar.Devices.ListAsync()).Where(d => d.Kind == DeviceKinds.Machine).ShouldBeEmpty("hangar took its own key back");
    }

    [Fact]
    public async Task An_address_that_answers_as_another_machine_never_gets_the_key()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        await world.StartAsync("workbench");
        using var peer = Client(atlas, atlas.OwnerToken);
        var seen = new List<string?>();
        world.Router.Seen = request => { if (request.RequestUri!.Host == "workbench.test") seen.Add(request.Headers.Authorization?.Parameter); };

        // hangar says it is at workbench's address: workbench answers as workbench, so nothing is sent there.
        var stranger = await peer.PostAsJsonAsync("/api/machine/introductions",
            new Introduction(hangar.Id, "x", null, ["https://workbench.test"], "fdt_x.y", null));
        (await stranger.ReadSuccessAsync()).GetProperty("status").GetString().ShouldBe("unreachable");
        seen.Where(t => t is not null).ShouldBeEmpty("no token went to the wrong machine");
    }

    [Fact]
    public async Task With_Your_Fleet_off_a_machine_takes_no_introductions()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas", yourFleet: false);
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        (await atlas.ListAsync()).ShouldBeEmpty();
        (await atlas.Devices.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_another_machine_in_the_fleet_introduces_and_a_phone_does_not()
    {
        await using var world = new World();
        var atlas = await world.StartAsync("atlas");
        var (_, phoneToken) = await atlas.Devices.IssueAsync("Pixel 9", "android");
        using var phone = Client(atlas, phoneToken);

        (await phone.PostAsJsonAsync("/api/machine/introductions", new Introduction("m", "x", null, ["https://x.test"], "t", null)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_machine_removes_only_the_devices_it_made()
    {
        await using var world = new World();
        var atlas = await world.StartAsync("atlas");
        var (_, hangarKey) = await atlas.Devices.IssueAsync("hangar", "other", kind: DeviceKinds.Machine);
        var (_, workbenchKey) = await atlas.Devices.IssueAsync("workbench", "other", kind: DeviceKinds.Machine);
        var (phone, _) = await atlas.Devices.IssueAsync("Pixel 9", "android", pairedVia: "someone");
        using var hangar = Client(atlas, hangarKey);
        using var workbench = Client(atlas, workbenchKey);

        var made = (await (await hangar.PostAsJsonAsync("/api/machine/devices", new { name = "Chrome via hangar", platform = "other", pairedVia = "hangar-id" })).ReadSuccessAsync())
            .GetProperty("deviceId").GetString();

        (await workbench.DeleteAsync($"/api/machine/devices/{made}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "workbench didn't make it");
        (await hangar.DeleteAsync($"/api/machine/devices/{phone.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a phone paired here isn't a machine's");
        (await hangar.DeleteAsync($"/api/machine/devices/{made}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var owner = Client(atlas, atlas.OwnerToken);
        (await owner.DeleteAsync($"/api/machine/devices/{phone.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent, "the owner removes anything");
    }

    // ---- scaffolding ----

    private sealed class Router : HttpMessageHandler
    {
        public Dictionary<string, HttpMessageHandler> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<HttpRequestMessage>? Seen { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen?.Invoke(request);
            var host = request.RequestUri!.Host;
            var name = host.EndsWith(".test", StringComparison.Ordinal) ? host[..^5] : host;
            if (!Targets.TryGetValue(name, out var target) || Down.Contains(name))
                throw new HttpRequestException("connection refused");
            return await new HttpMessageInvoker(target, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class Node(string name, ApiWebApplicationFactory factory, List<string> addresses)
    {
        public string Name { get; } = name;

        public ApiWebApplicationFactory Factory { get; } = factory;

        public List<string> Addresses { get; } = addresses;

        public string Id => Factory.Services.GetRequiredService<MachineIdentityStore>().Get().Id;

        public string OwnerToken => Factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

        public RemoteMachineService Machines => Factory.Services.GetRequiredService<RemoteMachineService>();

        public FleetRoster Roster => Factory.Services.GetRequiredService<FleetRoster>();

        public DeviceTokenService Devices => Factory.Services.GetRequiredService<DeviceTokenService>();

        public Task<IReadOnlyList<RemoteMachine>> ListAsync() => Machines.ListAsync();

        public Task SyncAllAsync() => Roster.SyncAllAsync(CancellationToken.None);
    }

    private sealed class FixedAddresses(List<string> addresses) : ISelfAddresses
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(addresses.ToList());
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly List<Node> _nodes = [];

        public Router Router { get; } = new();

        public Dictionary<string, Node> Machines { get; } = [];

        public async Task<Node> StartAsync(string name, bool yourFleet = true)
        {
            var addresses = new List<string> { $"https://{name}.test" };
            var factory = new ApiWebApplicationFactory(
                authEnabled: false,
                tokenAuthEnabled: true,
                simulateLocalhostRequest: true,
                host: "0.0.0.0",
                configureTestServices: services =>
                {
                    services.AddHttpClient(RemoteMachineService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Router);
                    services.AddSingleton<ISelfAddresses>(new FixedAddresses(addresses));
                });
            Router.Targets[name] = factory.Server.CreateHandler();
            var node = new Node(name, factory, addresses);
            _nodes.Add(node);
            Machines[node.Id] = node;
            factory.Services.GetRequiredService<MachineIdentityStore>().Update(identity => identity with { Name = name });
            if (yourFleet)
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(YourFleet.PreferenceKey, "true");
            }

            return node;
        }

        /// <summary>The owner adds <paramref name="added"/> on <paramref name="through"/> with its machine token.</summary>
        public static async Task AddAsync(Node through, Node added)
        {
            var (machine, error) = await through.Machines.AddAsync($"https://{added.Name}.test", added.OwnerToken, CancellationToken.None);
            machine.ShouldNotBeNull(error?.Message);
        }

        /// <summary>What the background sync does, run until nothing changes.</summary>
        public async Task SettleAsync()
        {
            for (var round = 0; round < 3; round++)
                foreach (var node in _nodes)
                    await node.SyncAllAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes)
                await node.Factory.DisposeAsync();
        }
    }

    private static HttpClient Client(Node node, string token)
    {
        var client = node.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
