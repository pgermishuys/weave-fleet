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
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// A phone paired with hangar (its home) getting its own token on falcon, another machine in hangar's list, through
/// hangar; and losing it there when it's removed on hangar. Hangar reaches falcon through falcon's test server.
/// </summary>
[Collection(TwoFleetTestGroup.Name)]
public sealed class DeviceGrantTests
{
    [Fact]
    public async Task A_phone_gets_its_own_token_on_another_machine_which_works_there()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        var (phoneDevice, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);

        var granted = await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null);

        var grant = await granted.ReadSuccessAsync();
        grant.GetProperty("machineId").GetString().ShouldBe(MachineId(falcon));
        grant.GetProperty("baseUrl").GetString().ShouldBe("https://falcon.test");
        var falconToken = grant.GetProperty("token").GetString()!;
        falconToken.ShouldStartWith("fdt_");
        falconToken.ShouldNotBe(MachineToken(falcon));

        using var onFalcon = Client(falcon, falconToken);
        (await onFalcon.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await onFalcon.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var minted = (await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync()).Single();
        minted.Name.ShouldBe($"Pixel 9 via {Name(hangar)}");
        minted.PairedVia.ShouldBe(MachineId(hangar));
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().GetGrantAsync(phoneDevice.Id, MachineId(falcon)))!
            .RemoteDeviceId.ShouldBe(minted.Id);
    }

    [Fact]
    public async Task Removing_the_phone_on_home_removes_it_on_the_other_machine()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        var (phoneDevice, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        var falconToken = (await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync()).GetProperty("token").GetString()!;
        using var owner = Client(hangar, MachineToken(hangar));

        (await owner.DeleteAsync($"/api/machine/devices/{phoneDevice.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var onFalcon = Client(falcon, falconToken);
        (await onFalcon.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().ListGrantsForDeviceAsync(phoneDevice.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_again_replaces_the_old_grant()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        var (_, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);

        var first = (await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync()).GetProperty("token").GetString()!;
        var second = (await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync()).GetProperty("token").GetString()!;

        (await Client(falcon, first).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client(falcon, second).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Only_machines_in_homes_list_and_only_paired_devices()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        var (_, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        using var owner = Client(hangar, MachineToken(hangar));

        (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await ListAsync(hangar, falcon);
        (await owner.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_machine_that_is_away_gets_the_removal_when_it_answers_again()
    {
        await using var falcon = Fleet();
        var route = new SwitchableHandler();
        await using var hangar = Fleet(handler: route);
        route.Target = falcon.Server.CreateHandler();
        await ListAsync(hangar, falcon);
        var (phoneDevice, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        var falconToken = (await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync()).GetProperty("token").GetString()!;

        route.Down = true;
        using var owner = Client(hangar, MachineToken(hangar));
        (await owner.DeleteAsync($"/api/machine/devices/{phoneDevice.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK, "falcon hasn't heard yet");
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().ListRevokedGrantsAsync()).Count.ShouldBe(1);

        route.Down = false;
        await hangar.Services.GetRequiredService<DeviceGrantService>().RetryRevocationsAsync(MachineId(falcon), CancellationToken.None);

        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().ListRevokedGrantsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_grant_waits_until_the_old_token_on_the_other_machine_is_gone()
    {
        await using var falcon = Fleet();
        var route = new SwitchableHandler();
        await using var hangar = Fleet(handler: route);
        route.Target = falcon.Server.CreateHandler();
        await ListAsync(hangar, falcon);
        var (phoneDevice, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync();
        var first = (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().GetGrantAsync(phoneDevice.Id, MachineId(falcon)))!;

        route.Down = true;
        (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().GetGrantAsync(phoneDevice.Id, MachineId(falcon)))!
            .RemoteDeviceId.ShouldBe(first.RemoteDeviceId, "hangar still knows the token it couldn't remove");

        route.Down = false;
        await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync();
        var onFalcon = await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync();
        onFalcon.Count.ShouldBe(1);
        onFalcon.Single().Id.ShouldNotBe(first.RemoteDeviceId);
    }

    [Fact]
    public async Task A_grant_that_finishes_after_the_phone_was_removed_is_undone()
    {
        await using var falcon = Fleet();
        var route = new SwitchableHandler();
        await using var hangar = Fleet(handler: route);
        route.Target = falcon.Server.CreateHandler();
        await ListAsync(hangar, falcon);
        var (phoneDevice, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        route.BeforeSend = async request =>
        {
            if (request.Method == HttpMethod.Post)
                await hangar.Services.GetRequiredService<DeviceTokenService>().RevokeAsync(phoneDevice.Id);
        };

        (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).IsSuccessStatusCode.ShouldBeFalse();

        (await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync()).ShouldBeEmpty();
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().ListGrantsForMachineAsync(MachineId(falcon))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_machine_from_the_list_removes_phones_tokens_on_it()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        var (_, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);
        var falconToken = (await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync()).GetProperty("token").GetString()!;

        using var owner = Client(hangar, MachineToken(hangar));
        (await owner.DeleteAsync($"/api/machines/{MachineId(falcon)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().ListGrantsForMachineAsync(MachineId(falcon))).ShouldBeEmpty();
    }

    // ---- Your Fleet: a screen signed in as the owner gets its own key ----

    [Fact]
    public async Task An_owner_screen_gets_its_own_token_on_another_machine()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        using var screen = Client(hangar, MachineToken(hangar));

        var granted = await screen.PostAsJsonAsync($"/api/machines/{MachineId(falcon)}/device-grant", new { screenId = "s-1234", name = "Chrome on a laptop" });

        var falconToken = (await granted.ReadSuccessAsync()).GetProperty("token").GetString()!;
        falconToken.ShouldStartWith("fdt_");
        falconToken.ShouldNotBe(MachineToken(falcon));
        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client(falcon, falconToken).GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var minted = (await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync()).Single();
        minted.Name.ShouldBe($"Chrome on a laptop via {Name(hangar)}");
        minted.PairedVia.ShouldBe(MachineId(hangar));
        (await hangar.Services.GetRequiredService<IRemoteMachineRepository>().GetGrantAsync("screen:s-1234", MachineId(falcon)))!
            .RemoteDeviceId.ShouldBe(minted.Id);
        (await hangar.Services.GetRequiredService<DeviceTokenService>().ListAsync()).Where(d => d.Kind == DeviceKinds.Device).ShouldBeEmpty("a screen isn't a paired device on hangar");
    }

    [Fact]
    public async Task A_screen_asking_again_replaces_its_key_and_another_screen_keeps_its_own()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        using var screen = Client(hangar, MachineToken(hangar));
        async Task<string> AskAsync(string screenId) =>
            (await (await screen.PostAsJsonAsync($"/api/machines/{MachineId(falcon)}/device-grant", new { screenId })).ReadSuccessAsync()).GetProperty("token").GetString()!;

        var first = await AskAsync("one");
        var other = await AskAsync("two");
        var second = await AskAsync("one");

        (await Client(falcon, first).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client(falcon, second).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client(falcon, other).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Removing_the_machine_takes_a_screens_key_on_it_away()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        using var owner = Client(hangar, MachineToken(hangar));
        var falconToken = (await (await owner.PostAsJsonAsync($"/api/machines/{MachineId(falcon)}/device-grant", new { screenId = "one" })).ReadSuccessAsync()).GetProperty("token").GetString()!;

        (await owner.DeleteAsync($"/api/machines/{MachineId(falcon)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bad_screen_id_is_refused()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        using var owner = Client(hangar, MachineToken(hangar));

        (await owner.PostAsJsonAsync($"/api/machines/{MachineId(falcon)}/device-grant", new { screenId = "has spaces!" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await falcon.Services.GetRequiredService<DeviceTokenService>().ListAsync()).ShouldBeEmpty();
    }

    // ---- The native phone app: reads the list, then asks for a key for every machine it lacks ----

    [Fact]
    public async Task The_phone_app_reads_the_list_without_tokens_then_gets_keys_for_each_machine()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        await ListAsync(hangar, falcon);
        var (_, phoneToken) = await hangar.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);

        var list = (await (await phone.GetAsync("/api/machines")).ReadSuccessAsync()).GetProperty("machines");
        var listed = list.EnumerateArray().Single();
        listed.GetProperty("id").GetString().ShouldBe(MachineId(falcon));
        listed.GetProperty("baseUrl").GetString().ShouldBe("https://falcon.test");
        listed.TryGetProperty("token", out var token).ShouldBeTrue();
        token.ValueKind.ShouldBe(JsonValueKind.Null, "a phone never sees a machine's token");

        var granted = await (await phone.PostAsync($"/api/machines/{MachineId(falcon)}/device-grant", null)).ReadSuccessAsync();
        (await Client(falcon, granted.GetProperty("token").GetString()!).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- A machine added by its pairing code holds a machine-kind device token, not the machine token ----

    [Fact]
    public async Task A_machine_added_by_pairing_code_can_make_and_remove_a_screens_key()
    {
        await using var falcon = Fleet();
        await using var hangar = Fleet(routeTo: falcon);
        var (_, peerToken) = await falcon.Services.GetRequiredService<DeviceTokenService>().IssueAsync("hangar", "other", kind: DeviceKinds.Machine);
        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(MachineId(falcon), "falcon", "https://falcon.test", peerToken, "linux", null)]);
        using var owner = Client(hangar, MachineToken(hangar));
        var falconToken = (await (await owner.PostAsJsonAsync($"/api/machines/{MachineId(falcon)}/device-grant", new { screenId = "one" })).ReadSuccessAsync()).GetProperty("token").GetString()!;
        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Removing the machine takes the key away, through the peer token.
        (await owner.DeleteAsync($"/api/machines/{MachineId(falcon)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client(falcon, falconToken).GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_peer_machine_token_removes_nothing_but_devices_a_machine_made_and_a_phone_token_makes_nothing()
    {
        await using var falcon = Fleet();
        var devices = falcon.Services.GetRequiredService<DeviceTokenService>();
        var (_, peerToken) = await devices.IssueAsync("hangar", "other", kind: DeviceKinds.Machine);
        var (phone, phoneToken) = await devices.IssueAsync("Pixel 9", "android");
        using var peer = Client(falcon, peerToken);

        (await peer.DeleteAsync($"/api/machine/devices/{phone.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client(falcon, phoneToken).PostAsJsonAsync("/api/machine/devices", new { name = "x", platform = "other" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var made = await peer.PostAsJsonAsync("/api/machine/devices", new { name = "Chrome via hangar", platform = "other", pairedVia = "hangar-id" });
        var madeId = (await made.ReadSuccessAsync()).GetProperty("deviceId").GetString();
        (await peer.DeleteAsync($"/api/machine/devices/{madeId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task ListAsync(WebApplicationFactory<Program> home, WebApplicationFactory<Program> other) =>
        await home.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(MachineId(other), "falcon", "https://falcon.test", MachineToken(other), "linux", null)]);

    private sealed class SwitchableHandler : DelegatingHandler
    {
        public HttpMessageHandler? Target { get; set; }

        public bool Down { get; set; }

        public Func<HttpRequestMessage, Task>? BeforeSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
                throw new HttpRequestException("connection refused");
            if (BeforeSend is { } before)
                await before(request);
            return await new HttpMessageInvoker(Target!, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private static ApiWebApplicationFactory Fleet(ApiWebApplicationFactory? routeTo = null, HttpMessageHandler? handler = null) =>
        new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: "0.0.0.0",
            configureTestServices: services =>
            {
                if (routeTo is null && handler is null)
                    return;
                services.AddHttpClient(RemoteMachineService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler ?? routeTo!.Server.CreateHandler());
            });

    private static string MachineToken(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static string MachineId(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<MachineIdentityStore>().Get().Id;

    private static string Name(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<MachineIdentityStore>().Get().Name ?? Environment.MachineName;

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
