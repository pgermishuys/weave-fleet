using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The machine list on the server: adding another Fleet (checked against it), importing a browser's list, removing,
/// and what a paired device may see. Two Fleets in one process: A's outbound requests are routed to B's test server.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class MachinesEndpointTests
{
    private const string FalconUrl = "https://falcon.tail9c2e.ts.net";

    [Fact]
    public async Task The_owner_adds_a_machine_after_checking_it_and_sees_its_token()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        using var owner = Client(hangar, Token(hangar));

        var added = await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl + "/", token = Token(falcon) });

        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().ShouldBe(Identity(falcon).Id);
        body.GetProperty("baseUrl").GetString().ShouldBe(FalconUrl);
        body.GetProperty("status").GetString().ShouldBe("online");
        body.GetProperty("token").GetString().ShouldBe(Token(falcon));

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/machines");
        list.GetProperty("machines").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_wrong_token_or_address_is_refused_with_a_reason()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        using var owner = Client(hangar, Token(hangar));

        var wrongToken = await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = "not-the-token-0123456789" });
        wrongToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrongToken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("That token wasn't accepted.");

        (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = "ftp://falcon", token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = "https://user:pw@falcon", token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_machine_cannot_add_itself()
    {
        await using var hangar = Fleet(routeToSelf: true);
        using var owner = Client(hangar, Token(hangar));

        var added = await owner.PostAsJsonAsync("/api/machines", new { baseUrl = "https://hangar.example", token = Token(hangar) });

        added.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("That's this machine.");
    }

    [Fact]
    public async Task A_device_reads_the_list_without_tokens_and_cannot_change_it()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        using var owner = Client(hangar, Token(hangar));
        await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) });
        var (_, deviceToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(hangar, deviceToken);

        var list = await phone.GetFromJsonAsync<JsonElement>("/api/machines");
        var entry = list.GetProperty("machines").EnumerateArray().Single();
        entry.GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        entry.GetProperty("token").ValueKind.ShouldBe(JsonValueKind.Null);
        list.GetRawText().ShouldNotContain(Token(falcon));

        (await phone.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.PutAsJsonAsync($"/api/machines/{entry.GetProperty("id").GetString()}", new { name = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.DeleteAsync($"/api/machines/{entry.GetProperty("id").GetString()}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.PostAsJsonAsync("/api/machines/import", new { machines = Array.Empty<object>() })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Importing_a_browsers_list_is_idempotent_and_skips_this_machine()
    {
        await using var hangar = Fleet();
        using var owner = Client(hangar, Token(hangar));
        var machines = new object[]
        {
            new { id = "falcon-id", name = "falcon", baseUrl = "http://100.64.90.72:2113", token = "falcon-token-0123456789", os = "linux", addedAt = "2026-09-26T10:00:00Z" },
            new { id = Identity(hangar).Id, name = "me", baseUrl = "http://127.0.0.1:2113", token = "x", os = "linux", addedAt = "2026-09-26T10:00:00Z" },
            new { id = "bad", name = "bad", baseUrl = "javascript:alert(1)", token = "x", os = "linux", addedAt = "2026-09-26T10:00:00Z" },
        };

        var first = await owner.PostAsJsonAsync("/api/machines/import", new { machines });
        var second = await owner.PostAsJsonAsync("/api/machines/import", new { machines });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("machines").EnumerateArray().ToList();
        list.Select(m => m.GetProperty("id").GetString()).ShouldBe(["falcon-id"]);
        list[0].GetProperty("token").GetString().ShouldBe("falcon-token-0123456789");
        list[0].GetProperty("status").GetString().ShouldBe("unknown");
        list[0].GetProperty("addedAt").GetDateTimeOffset().ShouldBe(DateTimeOffset.Parse("2026-09-26T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Removing_a_machine()
    {
        await using var hangar = Fleet();
        using var owner = Client(hangar, Token(hangar));
        await owner.PostAsJsonAsync("/api/machines/import", new { machines = new[] { new { id = "falcon-id", name = "falcon", baseUrl = "http://falcon:2113", token = "falcon-token-0123456789" } } });

        (await owner.DeleteAsync("/api/machines/falcon-id")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.DeleteAsync("/api/machines/falcon-id")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetFromJsonAsync<JsonElement>("/api/machines")).GetProperty("machines").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_token_is_kept_encrypted()
    {
        await using var hangar = Fleet();
        using var owner = Client(hangar, Token(hangar));
        await owner.PostAsJsonAsync("/api/machines/import", new { machines = new[] { new { id = "falcon-id", name = "falcon", baseUrl = "http://falcon:2113", token = "falcon-token-0123456789" } } });

        var stored = await hangar.Services.GetRequiredService<WeaveFleet.Domain.Repositories.IRemoteMachineRepository>().GetAsync("falcon-id");

        stored!.EncryptedToken.ShouldNotContain("falcon-token");
    }

    [Fact]
    public async Task Only_the_owner_lets_agents_hand_work_to_a_machine_and_adding_it_again_keeps_that()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        using var owner = Client(hangar, Token(hangar));
        var id = Identity(falcon).Id;
        var added = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).Content.ReadFromJsonAsync<JsonElement>();
        added.GetProperty("agentsAllowed").GetBoolean().ShouldBeFalse();

        var allowed = await owner.PutAsJsonAsync($"/api/machines/{id}", new { agentsAllowed = true });
        await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) });
        var (_, deviceToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(hangar, deviceToken);
        var fromPhone = await phone.PutAsJsonAsync($"/api/machines/{id}", new { agentsAllowed = false });

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentsAllowed").GetBoolean().ShouldBeTrue();
        fromPhone.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/machines");
        list.GetProperty("machines")[0].GetProperty("agentsAllowed").GetBoolean().ShouldBeTrue();
    }

    private static ApiWebApplicationFactory Fleet(ApiWebApplicationFactory? routeTo = null, bool routeToSelf = false)
    {
        ApiWebApplicationFactory? self = null;
        self = new ApiWebApplicationFactory(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: "0.0.0.0",
            configureTestServices: services =>
            {
                if (routeTo is null && !routeToSelf)
                    return;
                services.AddHttpClient(RemoteMachineService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => (routeToSelf ? self! : routeTo!).Server.CreateHandler());
            });
        return self;
    }

    private static string Token(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static MachineIdentity Identity(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<MachineIdentityStore>().Get();

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
