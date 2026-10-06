using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Pairing a phone: the owner makes a one-time code, the phone previews and redeems it for its own token, and the
/// owner can list and remove devices. Requests arrive over loopback on a reachable bind, as through tailscale serve.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class PairingEndpointTests
{
    private const string PhoneUrl = "https://hangar.tail9c2e.ts.net";

    [Fact]
    public async Task Create_preview_and_redeem_works_once()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));
        using var phone = Client(factory);

        var created = await CreateCodeAsync(owner);
        var secret = created.GetProperty("secret").GetString()!;

        var preview = await phone.PostAsJsonAsync("/api/pairing/preview", new { secret });
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var previewed = await preview.Content.ReadFromJsonAsync<JsonElement>();
        previewed.GetProperty("machineName").GetString().ShouldNotBeNullOrEmpty();
        previewed.GetProperty("machineId").GetString().ShouldBe(created.GetProperty("payload").GetProperty("machineId").GetString());

        var redeemed = await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "  Pixel 9  ", platform = "android" });
        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        token.ShouldStartWith("fdt_");
        body.GetProperty("machine").GetProperty("apiVersion").GetInt32().ShouldBe(1);
        redeemed.Headers.GetValues("Set-Cookie").ShouldContain(h => h.StartsWith(".WeaveFleet.Auth=", StringComparison.Ordinal));

        using var paired = Client(factory, token);
        (await paired.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var again = await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "Pixel", platform = "android" });
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var devices = await owner.GetFromJsonAsync<JsonElement>("/api/machine/devices");
        var listed = devices.GetProperty("devices").EnumerateArray().Single();
        listed.GetProperty("name").GetString().ShouldBe("Pixel 9");
        listed.GetProperty("platform").GetString().ShouldBe("android");
        listed.TryGetProperty("tokenHash", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_qr_url_carries_the_v1_payload_in_its_fragment()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));

        var created = await CreateCodeAsync(owner, PhoneUrl + "/");

        var url = created.GetProperty("url").GetString()!;
        url.ShouldStartWith(PhoneUrl + "/pair#p=");
        url.ShouldNotContain("?");
        var fragment = url[(url.IndexOf("#p=", StringComparison.Ordinal) + 3)..];
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(FromBase64Url(fragment))).RootElement;
        json.GetProperty("v").GetInt32().ShouldBe(1);
        json.GetProperty("url").GetString().ShouldBe(PhoneUrl);
        json.GetProperty("secret").GetString().ShouldBe(created.GetProperty("secret").GetString());
        json.GetProperty("machineId").GetString().ShouldNotBeNullOrEmpty();
        json.GetProperty("machineName").GetString().ShouldNotBeNullOrEmpty();
        created.GetProperty("manualCode").GetString()!.ShouldMatch("^[0-9A-Z]{4}-[0-9A-Z]{4}$");
    }

    [Fact]
    public async Task The_manual_code_redeems_too()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));
        using var phone = Client(factory);
        var created = await CreateCodeAsync(owner);
        var manualCode = created.GetProperty("manualCode").GetString()!.ToLowerInvariant();

        (await phone.PostAsJsonAsync("/api/pairing/preview", new { manualCode })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var redeemed = await phone.PostAsJsonAsync("/api/pairing/redeem", new { manualCode, deviceName = "iPhone", platform = "ios" });

        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_code_expires_after_ten_minutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(services => services.AddSingleton(new PairingCodeStore(time)));
        using var owner = Client(factory, MachineToken(factory));
        using var phone = Client(factory);
        var secret = (await CreateCodeAsync(owner)).GetProperty("secret").GetString();

        time.Advance(TimeSpan.FromMinutes(10));

        (await phone.PostAsJsonAsync("/api/pairing/preview", new { secret })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "Pixel", platform = "android" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_bad_device_name_does_not_use_up_the_code()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));
        using var phone = Client(factory);
        var secret = (await CreateCodeAsync(owner)).GetProperty("secret").GetString();

        (await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "   ", platform = "android" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = new string('x', 61), platform = "android" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "Pixel", platform = "palm" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await phone.PostAsJsonAsync("/api/pairing/redeem", new { secret, deviceName = "Pixel", platform = "android" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pairing_needs_a_full_http_address()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));

        (await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "hangar" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "javascript:alert(1)" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "https://user:pw@hangar" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Devices_cannot_pair_list_remove_or_mint()
    {
        await using var factory = CreateFactory();
        var (device, token) = await factory.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(factory, token);

        (await phone.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = PhoneUrl })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.GetAsync("/api/machine/devices")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.PostAsJsonAsync("/api/machine/devices", new { name = "x", platform = "ios" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anyone_may_try_a_code_without_signing_in()
    {
        await using var factory = CreateFactory();
        using var stranger = Client(factory);

        (await stranger.PostAsJsonAsync("/api/pairing/preview", new { secret = "nope" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync("/api/machine/devices")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Removing_a_device_locks_its_token_out_at_once()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));
        var (device, token) = await factory.Services.GetRequiredService<DeviceTokenService>().IssueAsync("Pixel", "android");
        using var phone = Client(factory, token);
        (await phone.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await phone.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_the_machine_token_mints_a_device_for_another_machine()
    {
        await using var factory = CreateFactory(host: "127.0.0.1");
        using var byToken = Client(factory, MachineToken(factory));
        using var byLoopback = Client(factory);

        var minted = await byToken.PostAsJsonAsync("/api/machine/devices", new { name = "Pixel via hangar", platform = "android", pairedVia = "home-id" });
        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await minted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        (await factory.Services.GetRequiredService<DeviceTokenService>().ValidateAsync(token)).ShouldNotBeNull();
        (await factory.Services.GetRequiredService<DeviceTokenService>().ListAsync()).Single().PairedVia.ShouldBe("home-id");

        (await byLoopback.PostAsJsonAsync("/api/machine/devices", new { name = "x", platform = "ios" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Typed_codes_are_limited_to_five_tries_a_minute()
    {
        await using var factory = CreateFactory();
        using var stranger = Client(factory);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await stranger.PostAsJsonAsync("/api/pairing/preview", new { manualCode = "AAAA-AAAA" })).StatusCode);

        statuses.Take(5).ShouldAllBe(s => s == HttpStatusCode.NotFound);
        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_typed_code_counts_even_with_a_secret_beside_it()
    {
        await using var factory = CreateFactory();
        using var stranger = Client(factory);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await stranger.PostAsJsonAsync("/api/pairing/preview", new { secret = $"guess-{i}", manualCode = "AAAA-AAAA" })).StatusCode);

        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Pairing_requests_are_limited_to_ten_a_minute()
    {
        await using var factory = CreateFactory();
        using var stranger = Client(factory);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await stranger.PostAsJsonAsync("/api/pairing/preview", new { secret = $"guess-{i}" })).StatusCode);

        statuses.Take(10).ShouldAllBe(s => s == HttpStatusCode.NotFound);
        statuses[10].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_phone_address_is_saved_on_the_machine()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));

        var saved = await owner.PutAsJsonAsync("/api/machine", new { publicUrl = PhoneUrl + "/" });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicUrl").GetString().ShouldBe(PhoneUrl);

        // Renaming leaves the address alone; an empty address clears it.
        await owner.PutAsJsonAsync("/api/machine", new { name = "hangar" });
        (await owner.GetFromJsonAsync<JsonElement>("/api/machine")).GetProperty("publicUrl").GetString().ShouldBe(PhoneUrl);
        (await owner.PutAsJsonAsync("/api/machine", new { publicUrl = "ftp://x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await owner.PutAsJsonAsync("/api/machine", new { publicUrl = "" });
        (await owner.GetFromJsonAsync<JsonElement>("/api/machine")).GetProperty("publicUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static async Task<JsonElement> CreateCodeAsync(HttpClient owner, string baseUrl = PhoneUrl)
    {
        var response = await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string MachineToken(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static ApiWebApplicationFactory CreateFactory(Action<IServiceCollection>? services = null, string host = "0.0.0.0")
        => new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: host, configureTestServices: services);

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
