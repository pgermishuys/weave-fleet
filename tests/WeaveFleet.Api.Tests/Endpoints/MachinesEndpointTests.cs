using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The machine list on the server: adding another Fleet (checked against it), importing a browser's list, removing,
/// and what a paired device may see. Two Fleets in one process: A's outbound requests are routed to B's test server.
/// </summary>
[Collection(TwoFleetTestGroup.Name)]
public sealed class MachinesEndpointTests : IClassFixture<MachinesEndpointTests.Server>, IAsyncLifetime
{
    private const string FalconUrl = "https://falcon.tail9c2e.ts.net";

    private readonly ApiWebApplicationFactory _falcon;
    private readonly ApiWebApplicationFactory _hangar;

    public MachinesEndpointTests(Server server)
    {
        _falcon = server.Falcon;
        _hangar = server.Hangar;
    }

    /// <summary>Every test starts with an empty machine list on hangar.</summary>
    public async Task InitializeAsync()
    {
        var machines = _hangar.Services.GetRequiredService<WeaveFleet.Domain.Repositories.IRemoteMachineRepository>();
        foreach (var machine in await machines.ListAsync())
            await machines.DeleteAsync(machine.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_owner_adds_a_machine_after_checking_it_and_sees_its_token()
    {
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));

        var added = await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl + "/", token = Token(falcon) });

        var body = await added.ReadSuccessAsync();
        body.GetProperty("id").GetString().ShouldBe(Identity(falcon).Id);
        body.GetProperty("baseUrl").GetString().ShouldBe(FalconUrl);
        body.GetProperty("status").GetString().ShouldBe("online");
        body.GetProperty("token").GetString().ShouldBe(Token(falcon));

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/machines");
        list.GetProperty("machines").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Set_up_from_here_lists_candidates_with_the_commands_and_refuses_a_destination_that_could_be_an_ssh_option()
    {
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));

        var candidates = await owner.GetAsync("/api/machines/candidates");
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await candidates.Content.ReadFromJsonAsync<JsonElement>();
        list.GetProperty("candidates").ValueKind.ShouldBe(JsonValueKind.Array);
        list.GetProperty("commands").GetArrayLength().ShouldBe(SshMachineService.Commands.Count);

        var probe = await owner.PostAsJsonAsync("/api/machines/candidates/probe", new { destination = "-oProxyCommand=touch-pwned" });
        probe.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probed = await probe.Content.ReadFromJsonAsync<JsonElement>();
        probed.GetProperty("state").GetString().ShouldBe("cantLogIn");
        probed.GetProperty("error").GetString()!.ShouldContain("isn't an SSH host");
    }

    [Fact]
    public async Task Setting_up_a_destination_that_could_be_an_ssh_option_fails_the_job_and_runs_nothing()
    {
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));

        var started = await (await owner.PostAsJsonAsync("/api/machines/setup", new { destination = "-oProxyCommand=touch-pwned" })).ReadSuccessAsync();

        var id = started.GetProperty("id").GetString();
        JsonElement job = default;
        for (var i = 0; i < 100; i++)
        {
            job = await owner.GetFromJsonAsync<JsonElement>($"/api/machines/setup/{id}");
            if (job.GetProperty("status").GetString() != "running")
                break;
            await Task.Delay(20);
        }

        job.GetProperty("status").GetString().ShouldBe("failed");
        job.GetProperty("error").GetString()!.ShouldContain("isn't an SSH host");
        (await owner.GetAsync("/api/machines/setup/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Adding_a_machine_keeps_the_address_given_and_learns_the_ones_it_reports()
    {
        using var owner = Client(_hangar, Token(_hangar));

        var body = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(_falcon) })).ReadSuccessAsync();

        var addresses = body.GetProperty("addresses").EnumerateArray().ToList();
        var given = addresses.Single(a => a.GetProperty("url").GetString() == FalconUrl);
        given.GetProperty("kind").GetString().ShouldBe("tailnet");
        given.GetProperty("learned").GetBoolean().ShouldBeFalse();
        // Falcon listens on 0.0.0.0, so it reports at least its host name.
        addresses.Count(a => a.GetProperty("learned").GetBoolean()).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_owner_puts_a_machines_addresses_in_their_own_order_and_a_stale_order_is_refused()
    {
        using var owner = Client(_hangar, Token(_hangar));
        var id = (await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(_falcon) })).ReadSuccessAsync()).GetProperty("id").GetString()!;
        // Hangar's watcher connects to falcon and learns its addresses in the background, so the list can change
        // between reading it and sending an order, which is turned away. Like the page, read it again and resend.
        List<string> order = [];
        JsonElement? reordered = null;
        for (var attempt = 0; attempt < 10 && reordered is null; attempt++)
        {
            var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/machines")).GetProperty("machines").EnumerateArray().Single();
            order = [.. listed.GetProperty("addresses").EnumerateArray().Select(a => a.GetProperty("url").GetString()!).Reverse()];
            order.Count.ShouldBeGreaterThan(1, "falcon listens on 0.0.0.0, so it reports addresses besides the one it was added with");
            var put = await owner.PutAsJsonAsync($"/api/machines/{id}", new { addresses = order });
            if (put.StatusCode == HttpStatusCode.OK)
                reordered = await put.Content.ReadFromJsonAsync<JsonElement>();
            else
                put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        reordered.ShouldNotBeNull("the order kept being turned away");
        reordered.Value.GetProperty("addresses").EnumerateArray().Select(a => a.GetProperty("url").GetString()).ShouldBe(order);

        // One address short is never the machine's list.
        var stale = await owner.PutAsJsonAsync($"/api/machines/{id}", new { addresses = order.Skip(1) });
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anyone_can_ask_who_answers_at_an_address_but_nothing_else_without_the_token()
    {
        using var anonymous = _falcon.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var probe = await anonymous.GetFromJsonAsync<JsonElement>("/api/machine/id");
        probe.GetProperty("id").GetString().ShouldBe(Identity(_falcon).Id);
        probe.GetProperty("apiVersion").GetInt32().ShouldBe(1);
        probe.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "apiVersion"], ignoreOrder: true);

        (await anonymous.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A page on another machine reads it too, before it sends the token there.
        using var fromPage = new HttpRequestMessage(HttpMethod.Get, "/api/machine/id");
        fromPage.Headers.Add("Origin", "http://hangar.tail9c2e.ts.net:2113");
        using var answer = await anonymous.SendAsync(fromPage);
        answer.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain("http://hangar.tail9c2e.ts.net:2113");
    }

    [Fact]
    public async Task A_machine_reports_where_it_can_be_reached()
    {
        using var owner = Client(_falcon, Token(_falcon));

        var machine = await owner.GetFromJsonAsync<JsonElement>("/api/machine");

        var urls = machine.GetProperty("addresses").EnumerateArray().Select(a => a.GetProperty("url").GetString()!).ToList();
        urls.ShouldContain($"http://{Environment.MachineName.ToLowerInvariant()}:{_falcon.Services.GetRequiredService<FleetOptions>().Port}");
    }

    [Fact]
    public async Task A_machine_reached_by_address_has_no_ssh_fields()
    {
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));

        var body = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).ReadSuccessAsync();

        body.TryGetProperty("sshDestination", out var ssh).ShouldBeTrue();
        ssh.ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("tunnelUp").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_wrong_token_or_address_is_refused_with_a_reason()
    {
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));

        var wrongToken = await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = "not-the-token-0123456789" });
        wrongToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var refusal = await wrongToken.Content.ReadFromJsonAsync<JsonElement>();
        refusal.GetProperty("error").GetString().ShouldBe("That token wasn't accepted.");
        refusal.GetProperty("diagnosis").GetProperty("code").GetString().ShouldBe("key-rejected");
        refusal.GetProperty("diagnosis").GetProperty("steps").GetArrayLength().ShouldBe(6);

        (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = "ftp://falcon", token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = "https://user:pw@falcon", token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Checking_a_saved_machine_says_it_answers_then_which_step_broke_when_its_token_changes()
    {
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));
        var id = Identity(falcon).Id;
        (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var answering = await (await owner.PostAsync($"/api/machines/{id}/check", null)).Content.ReadFromJsonAsync<JsonElement>();
        answering.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        answering.GetProperty("diagnosis").ValueKind.ShouldBe(JsonValueKind.Null);

        (await owner.PostAsync("/api/machines/nope/check", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));
        await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).ReadSuccessAsync();
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
        var hangar = _hangar;
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
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));
        await owner.PostAsJsonAsync("/api/machines/import", new { machines = new[] { new { id = "falcon-id", name = "falcon", baseUrl = "http://falcon:2113", token = "falcon-token-0123456789" } } });

        (await owner.DeleteAsync("/api/machines/falcon-id")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.DeleteAsync("/api/machines/falcon-id")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetFromJsonAsync<JsonElement>("/api/machines")).GetProperty("machines").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_token_is_kept_encrypted()
    {
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));
        await owner.PostAsJsonAsync("/api/machines/import", new { machines = new[] { new { id = "falcon-id", name = "falcon", baseUrl = "http://falcon:2113", token = "falcon-token-0123456789" } } });

        var stored = await hangar.Services.GetRequiredService<WeaveFleet.Domain.Repositories.IRemoteMachineRepository>().GetAsync("falcon-id");

        stored!.EncryptedToken.ShouldNotContain("falcon-token");
    }

    [Fact]
    public async Task Only_the_owner_lets_agents_hand_work_to_a_machine_and_adding_it_again_keeps_that()
    {
        var falcon = _falcon;
        var hangar = _hangar;
        using var owner = Client(hangar, Token(hangar));
        var id = Identity(falcon).Id;
        var added = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).ReadSuccessAsync();
        added.GetProperty("agentsAllowed").GetBoolean().ShouldBeFalse();

        var allowed = await owner.PutAsJsonAsync($"/api/machines/{id}", new { agentsAllowed = true });
        await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).ReadSuccessAsync();
        var (_, deviceToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(hangar, deviceToken);
        var fromPhone = await phone.PutAsJsonAsync($"/api/machines/{id}", new { agentsAllowed = false });

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentsAllowed").GetBoolean().ShouldBeTrue();
        fromPhone.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/machines");
        list.GetProperty("machines")[0].GetProperty("agentsAllowed").GetBoolean().ShouldBeTrue();
    }

    public sealed class Server : IDisposable
    {
        public Server() => Hangar = Fleet(routeTo: Falcon);

        public ApiWebApplicationFactory Falcon { get; } = Fleet();

        public ApiWebApplicationFactory Hangar { get; }

        public void Dispose()
        {
            Hangar.Dispose();
            Falcon.Dispose();
        }
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
