using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Adding a machine with a code: a node (no web app) makes a one-time code for another Fleet, which redeems it for a
/// token of its own. The node lists that token as a machine and can remove it; the master token never moves.
/// </summary>
public sealed class MachinePairingEndpointTests : IClassFixture<MachinePairingEndpointTests.Server>
{
    private readonly ApiWebApplicationFactory _node;

    // Typed codes are limited to five tries a minute across the server, so the test that tries one twice has its own.
    public MachinePairingEndpointTests(Server server) => _node = server.Factory;

    [Fact]
    public async Task A_node_makes_a_code_for_another_machine_and_for_a_phone()
    {
        var factory = _node;
        using var owner = Client(factory, MasterToken(factory));

        var made = await owner.PostAsJsonAsync("/api/machine/pairing/code", new { });
        made.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await made.Content.ReadFromJsonAsync<JsonElement>();
        code.GetProperty("manualCode").GetString()!.ShouldMatch("^[0-9A-Z]{4}-[0-9A-Z]{4}$");
        code.GetProperty("machineName").GetString().ShouldNotBeNullOrEmpty();

        var phone = await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "https://harbor.example.test" });
        phone.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void A_code_carries_every_address_with_the_ones_given_first_and_none_twice()
    {
        var urls = PairingAddresses.For(
            "https://studio.example.test/",
            "https://studio.tail4f2a.ts.net",
            ["http://studio.tail4f2a.ts.net:6262", "http://192.168.1.20:6262", "HTTPS://studio.example.test", "http://studio:6262"]);

        urls.ShouldBe([
            "https://studio.example.test",
            "https://studio.tail4f2a.ts.net",
            "http://studio.tail4f2a.ts.net:6262",
            "http://192.168.1.20:6262",
            "http://studio:6262",
        ]);
        PairingAddresses.For(null, null, []).ShouldBeEmpty();
    }

    [Fact]
    public void The_phone_link_holds_the_first_address_and_all_of_them_for_a_phone_to_try()
    {
        var payload = new WeaveFleet.Api.Contracts.PairingPayload(1, "m1", "hangar", "http://hangar.ts.net:6262", "s3cret", ["http://hangar.ts.net:6262", "http://192.168.1.31:6262"]);

        var link = PairingAddresses.Link(payload);

        link.ShouldStartWith("http://hangar.ts.net:6262/pair#p=");
        var encoded = link[(link.IndexOf("#p=", StringComparison.Ordinal) + 3)..].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        using var json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        json.RootElement.GetProperty("v").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("url").GetString().ShouldBe("http://hangar.ts.net:6262");
        json.RootElement.GetProperty("urls").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void The_link_is_the_first_address_with_the_code_in_the_fragment()
    {
        MachineEndpoints.PairingLink(["http://100.64.90.72:2113/", "http://falcon:2113"], "K3MQ-9ZDT")
            .ShouldBe("http://100.64.90.72:2113/add-machine#K3MQ-9ZDT");
        MachineEndpoints.PairingLink([], "K3MQ-9ZDT").ShouldBeNull();
    }

    [Fact]
    public void The_tailnet_name_comes_before_the_tailnet_address()
    {
        var addresses = MachineEndpoints.ReachableAddresses(new FleetOptions { Host = "100.64.90.72", Port = 2113 }, "falcon.tail9c2e.ts.net");

        addresses.Select(a => a.Url).ShouldBe(["http://falcon.tail9c2e.ts.net:2113", "http://100.64.90.72:2113"]);
        MachineEndpoints.PairingLink(addresses.Select(a => a.Url).ToList(), "K3MQ-9ZDT")
            .ShouldBe("http://falcon.tail9c2e.ts.net:2113/add-machine#K3MQ-9ZDT");
    }

    [Fact]
    public void Without_a_tailnet_address_the_name_is_left_out()
    {
        MachineEndpoints.ReachableAddresses(new FleetOptions { Host = "192.168.1.20", Port = 2113 }, "falcon.tail9c2e.ts.net")
            .Select(a => a.Url).ShouldBe(["http://192.168.1.20:2113"]);
        MachineEndpoints.ReachableAddresses(new FleetOptions { Host = "100.64.90.72", Port = 2113 }, null)
            .Select(a => a.Url).ShouldBe(["http://100.64.90.72:2113"]);
    }

    [Fact]
    public async Task Looking_at_a_code_names_the_machine_and_its_version_without_using_the_code_up()
    {
        await using var factory = CreateNode();
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);

        var preview = await other.PostAsJsonAsync("/api/pairing/preview", new { manualCode });

        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("machineName").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_code_redeems_on_a_node_for_a_machine_token_that_is_listed_and_can_be_removed()
    {
        var factory = _node;
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);

        var redeemed = await other.PostAsJsonAsync("/api/pairing/redeem",
            new { manualCode, deviceName = "studio-mac", platform = "other", kind = "machine" });

        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
        redeemed.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var body = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        token.ShouldStartWith("fdt_");
        token.ShouldNotBe(MasterToken(factory));

        using var paired = Client(factory, token);
        (await paired.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deviceId = token["fdt_".Length..token.IndexOf('.', StringComparison.Ordinal)];
        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/machine/devices")).GetProperty("devices").EnumerateArray()
            .Single(device => device.GetProperty("id").GetString() == deviceId);
        listed.GetProperty("name").GetString().ShouldBe("studio-mac");
        listed.GetProperty("kind").GetString().ShouldBe("machine");

        (await owner.DeleteAsync($"/api/machine/devices/{listed.GetProperty("id").GetString()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await paired.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_code_works_once()
    {
        await using var factory = CreateNode();
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);

        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_machine_token_cannot_make_codes_or_manage_devices()
    {
        var factory = _node;
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);
        var redeemed = await (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var paired = Client(factory, redeemed.GetProperty("token").GetString());

        (await paired.PostAsJsonAsync("/api/machine/pairing/code", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await paired.GetAsync("/api/machine/devices")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_and_keeps_the_code()
    {
        var factory = _node;
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);

        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "robot" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_machine_with_a_live_code_says_it_is_waiting_with_the_first_half_of_it_and_stops_when_the_code_is_used()
    {
        await using var factory = CreateNode();
        using var owner = Client(factory, MasterToken(factory));
        using var anyone = Client(factory);

        (await anyone.GetAsync("/api/pairing/waiting")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var manualCode = await NewCodeAsync(owner);
        var waiting = await anyone.GetAsync("/api/pairing/waiting");
        waiting.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await waiting.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hint").GetString().ShouldBe(manualCode[..4]);
        body.GetProperty("machineId").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        (await waiting.Content.ReadAsStringAsync()).ShouldNotContain(manualCode, Case.Sensitive, "the whole code is never said");

        (await anyone.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anyone.GetAsync("/api/pairing/waiting")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Making_a_code_needs_the_master_token()
    {
        var factory = _node;
        using var nobody = Client(factory);

        (await nobody.PostAsJsonAsync("/api/machine/pairing/code", new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = CreateNode();

        public void Dispose() => Factory.Dispose();
    }

    private static async Task<string> NewCodeAsync(HttpClient owner)
    {
        var made = await owner.PostAsJsonAsync("/api/machine/pairing/code", new { });
        made.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("manualCode").GetString()!;
    }

    private static ApiWebApplicationFactory CreateNode()
        => new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "127.0.0.1", requireToken: true, serveUi: false);

    private static string MasterToken(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
