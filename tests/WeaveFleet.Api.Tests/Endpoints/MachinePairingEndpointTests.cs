using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Adding a machine with a code: a node (no web app) makes a one-time code for another Fleet, which redeems it for a
/// token of its own. The node lists that token as a machine and can remove it; the master token never moves.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class MachinePairingEndpointTests
{
    [Fact]
    public async Task A_node_makes_a_code_for_another_machine_but_still_refuses_to_pair_a_phone()
    {
        await using var factory = CreateNode();
        using var owner = Client(factory, MasterToken(factory));

        var made = await owner.PostAsJsonAsync("/api/machine/pairing/code", new { });
        made.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await made.Content.ReadFromJsonAsync<JsonElement>();
        code.GetProperty("manualCode").GetString()!.ShouldMatch("^[0-9A-Z]{4}-[0-9A-Z]{4}$");
        code.GetProperty("machineName").GetString().ShouldNotBeNullOrEmpty();

        var phone = await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "https://harbor.example.test" });
        phone.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_code_redeems_on_a_node_for_a_machine_token_that_is_listed_and_can_be_removed()
    {
        await using var factory = CreateNode();
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

        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/machine/devices")).GetProperty("devices").EnumerateArray().Single();
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
        await using var factory = CreateNode();
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
        await using var factory = CreateNode();
        using var owner = Client(factory, MasterToken(factory));
        using var other = Client(factory);
        var manualCode = await NewCodeAsync(owner);

        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "robot" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await other.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "studio-mac", kind = "machine" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Making_a_code_needs_the_master_token()
    {
        await using var factory = CreateNode();
        using var nobody = Client(factory);

        (await nobody.PostAsJsonAsync("/api/machine/pairing/code", new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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
