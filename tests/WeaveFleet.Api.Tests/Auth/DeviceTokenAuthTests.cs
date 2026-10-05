using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Auth;

/// <summary>
/// A paired device's own token: accepted wherever the machine token is, kept out of managing access, and gone the
/// moment the device is removed. Requests arrive over loopback on a reachable bind, the way <c>tailscale serve</c>
/// delivers them, so none of them pass by ambient trust.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class DeviceTokenAuthTests
{
    private const string ForeignOrigin = "http://falcon.tail9c2e.ts.net:2113";

    [Fact]
    public async Task A_device_token_works_in_the_header()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var client = CreateClient(factory, token);

        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_token_works_on_the_hub_but_not_in_an_ordinary_query()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var client = CreateClient(factory);

        var hub = await client.PostAsync(
            $"/hubs/session-events/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            new StringContent(string.Empty));
        var api = await client.GetAsync($"/api/sessions?access_token={Uri.EscapeDataString(token)}");

        hub.StatusCode.ShouldBe(HttpStatusCode.OK);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_device_cannot_read_or_replace_the_machine_token()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var device = CreateClient(factory, token);
        using var owner = CreateClient(factory, MachineToken(factory));

        (await device.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.PostAsync("/api/machine/access/token", content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_cannot_change_where_pairing_codes_point()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var device = CreateClient(factory, token);
        using var owner = CreateClient(factory, MachineToken(factory));

        (await device.PutAsJsonAsync("/api/machine", new { publicUrl = "https://evil.example" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync("/api/machine", new { publicUrl = "https://hangar.tail1234.ts.net" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_device_token_is_refused_and_never_falls_back_to_loopback()
    {
        // Loopback bind: an untokened request would be let in. A presented device token must be right.
        await using var factory = CreateFactory(host: "127.0.0.1");
        var (device, _) = await IssueAsync(factory);
        var forged = $"fdt_{device.Id}.{new string('A', 43)}";
        using var client = CreateClient(factory, forged);

        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_removed_device_is_refused_in_the_header()
    {
        await using var factory = CreateFactory();
        var (device, token) = await IssueAsync(factory);
        using var client = CreateClient(factory, token);
        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await factory.Services.GetRequiredService<DeviceTokenService>().RevokeAsync(device.Id);

        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_device_cookie_carries_device_rights_and_dies_with_the_device()
    {
        await using var factory = CreateFactory();
        var (device, token) = await IssueAsync(factory);
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        (await browser.PostAsJsonAsync("/auth/token-login", new { token })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await factory.Services.GetRequiredService<DeviceTokenService>().RevokeAsync(device.Id);

        (await browser.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_owner_cookie_still_manages_access()
    {
        await using var factory = CreateFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        (await browser.PostAsJsonAsync("/auth/token-login", new { token = MachineToken(factory) })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await browser.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Loopback_auto_auth_is_the_owner()
    {
        await using var factory = CreateFactory(host: "127.0.0.1");
        using var client = CreateClient(factory);

        (await client.GetAsync("/api/machine/access")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Removing_a_device_closes_its_hub_connection_at_once()
    {
        await using var factory = CreateFactory();
        var (device, token) = await IssueAsync(factory);
        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}hubs/session-events", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync();
        var connections = factory.Services.GetRequiredService<DeviceConnections>();
        connections.CountFor(device.Id).ShouldBe(1);

        using var owner = CreateClient(factory, MachineToken(factory));
        (await owner.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        connections.CountFor(device.Id).ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_that_signed_in_just_before_its_device_was_removed_closes_when_tracked()
    {
        var devices = new DeviceTokenService(new WeaveFleet.Testing.Fakes.Repositories.InMemoryDeviceRepository(), TimeProvider.System);
        using var connections = new DeviceConnections(devices);
        var closes = 0;
        using var other = connections.Track("other", () => closes += 100);
        var (summary, _) = await devices.IssueAsync("Pixel", "android");
        (await devices.RevokeAsync(summary.Id)).ShouldBeTrue();

        using var late = connections.Track(summary.Id, () => closes++);

        closes.ShouldBe(1, "only the removed device's connection closes");
    }

    [Fact]
    public async Task A_tokened_device_request_from_another_origin_gets_cors_without_credentials()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var client = CreateClient(factory, token);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        request.Headers.Add("Origin", ForeignOrigin);
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ForeignOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public void Owner_rights_follow_the_scope_claim()
    {
        static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

        FleetClaims.IsOwner(User()).ShouldBeTrue("an old cookie without a scope is the owner's");
        FleetClaims.IsOwner(User(new Claim(FleetClaims.Scope, FleetClaims.Owner))).ShouldBeTrue();
        FleetClaims.IsOwner(User(new Claim(FleetClaims.Scope, FleetClaims.Device), new Claim(FleetClaims.DeviceId, "d"))).ShouldBeFalse();
        FleetClaims.IsOwner(User(new Claim(FleetClaims.DeviceId, "d"))).ShouldBeFalse();
        FleetClaims.IsOwner(User(new Claim(BearerTokenHandler.MethodClaim, BearerTokenHandler.AgentMethod))).ShouldBeFalse();
        FleetClaims.IsOwner(new ClaimsPrincipal(new ClaimsIdentity())).ShouldBeFalse();
    }

    private static Task<(DeviceSummary Device, string Token)> IssueAsync(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");

    private static string MachineToken(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static ApiWebApplicationFactory CreateFactory(string host = "0.0.0.0")
        => new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: host);

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
