using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Push;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// A browser registering where to push: saving, reading back and removing its subscription, a test push, and each
/// paired device keeping to its own.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class PushEndpointTests
{
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/phone-1";

    [Fact]
    public async Task The_vapid_key_is_this_machines()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));

        var key = await owner.GetFromJsonAsync<JsonElement>("/api/push/vapid");

        key.GetProperty("publicKey").GetString().ShouldBe(factory.Services.GetRequiredService<VapidKeyStore>().Get().PublicKey);
    }

    [Fact]
    public async Task A_device_subscribes_with_every_kind_by_default_then_changes_its_choice()
    {
        await using var factory = CreateFactory();
        var (device, token) = await IssueAsync(factory);
        using var phone = Client(factory, token);

        var saved = await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).ShouldBe(["permission", "question", "finished", "failed", "workflow"]);
        body.GetProperty("quietWhenDesk").GetBoolean().ShouldBeTrue();
        body.TryGetProperty("p256dh", out _).ShouldBeFalse();

        (await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint, kinds: ["permission", "question"], quiet: false))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var looked = await (await phone.PostAsJsonAsync("/api/push/subscriptions/lookup", new { endpoint = Endpoint })).Content.ReadFromJsonAsync<JsonElement>();
        looked.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).ShouldBe(["permission", "question"]);
        looked.GetProperty("quietWhenDesk").GetBoolean().ShouldBeFalse();

        var stored = (await Subscriptions(factory).ListAsync()).Single();
        stored.DeviceId.ShouldBe(device.Id);
    }

    [Fact]
    public async Task Bad_subscriptions_are_refused()
    {
        await using var factory = CreateFactory();
        using var owner = Client(factory, MachineToken(factory));

        (await owner.PutAsJsonAsync("/api/push/subscriptions", Body("http://insecure.example/x"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreach (var inside in new[] { "https://169.254.169.254/latest", "https://127.0.0.1/x", "https://[::1]/x", "https://localhost/x", "https://router/x", "https://nas.local/x" })
            (await owner.PutAsJsonAsync("/api/push/subscriptions", Body(inside))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, inside);
        (await owner.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint, kinds: ["everything"]))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/api/push/subscriptions", new { endpoint = Endpoint, keys = new { p256dh = "AAAA", auth = "AAAA" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint) with { channel = "apns" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_device_cannot_touch_another_devices_subscription_but_the_owner_can()
    {
        await using var factory = CreateFactory();
        var (_, first) = await IssueAsync(factory);
        var (_, second) = await IssueAsync(factory, "iPhone");
        using var phone = Client(factory, first);
        using var other = Client(factory, second);
        using var owner = Client(factory, MachineToken(factory));
        await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint));

        (await other.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.PostAsJsonAsync("/api/push/subscriptions/lookup", new { endpoint = Endpoint })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.SendAsync(Delete(Endpoint))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.PostAsJsonAsync("/api/push/test", new { endpoint = Endpoint })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await owner.PostAsJsonAsync("/api/push/subscriptions/lookup", new { endpoint = Endpoint })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.SendAsync(Delete(Endpoint))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Subscriptions(factory).ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rotated_subscription_keeps_the_old_ones_choices()
    {
        await using var factory = CreateFactory();
        var (_, token) = await IssueAsync(factory);
        using var phone = Client(factory, token);
        await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint, kinds: ["finished"], quiet: false));

        var rotated = await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint + "-new") with { kinds = null, quietWhenDesk = null, previousEndpoint = Endpoint });

        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var all = await Subscriptions(factory).ListAsync();
        all.Single().Endpoint.ShouldBe(Endpoint + "-new");
        all.Single().Kinds.ShouldBe(["finished"]);
        all.Single().QuietWhenDesk.ShouldBeFalse();
    }

    [Fact]
    public async Task Removing_a_device_removes_its_subscriptions()
    {
        await using var factory = CreateFactory();
        var (device, token) = await IssueAsync(factory);
        using var phone = Client(factory, token);
        using var owner = Client(factory, MachineToken(factory));
        await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint));
        await owner.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint + "-desk"));

        (await owner.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Subscriptions(factory).ListAsync()).Select(s => s.Endpoint).ShouldBe([Endpoint + "-desk"]);
    }

    [Fact]
    public async Task A_test_push_goes_to_that_subscription_only()
    {
        var sender = new RecordingSender(PushSendOutcome.Delivered);
        await using var factory = CreateFactory(services => services.AddSingleton<IPushSender>(sender));
        var (_, token) = await IssueAsync(factory);
        using var phone = Client(factory, token);
        await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint));
        await phone.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint + "-other"));

        var result = await phone.PostAsJsonAsync("/api/push/test", new { endpoint = Endpoint });

        (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().ShouldBe("delivered");
        sender.Sent.Single().Subscription.Endpoint.ShouldBe(Endpoint);
        var payload = JsonDocument.Parse(sender.Sent.Single().Message.Payload).RootElement;
        payload.GetProperty("v").GetInt32().ShouldBe(1);
        payload.GetProperty("title").GetString().ShouldBe("Fleet notifications work");
        payload.GetProperty("url").GetString().ShouldBe("/phone");
        sender.Sent.Single().Message.Payload.ShouldNotContain("fdt_");
        (await Subscriptions(factory).GetByEndpointAsync(Endpoint))!.LastSuccessAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_test_push_to_a_gone_subscription_removes_it()
    {
        var sender = new RecordingSender(PushSendOutcome.Gone);
        await using var factory = CreateFactory(services => services.AddSingleton<IPushSender>(sender));
        using var owner = Client(factory, MachineToken(factory));
        await owner.PutAsJsonAsync("/api/push/subscriptions", Body(Endpoint));

        var result = await owner.PostAsJsonAsync("/api/push/test", new { endpoint = Endpoint });

        (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().ShouldBe("gone");
        (await Subscriptions(factory).ListAsync()).ShouldBeEmpty();
    }

    private sealed class RecordingSender(PushSendOutcome outcome) : IPushSender
    {
        public List<(PushSubscriptionRecord Subscription, PushMessage Message)> Sent { get; } = [];

        public string Channel => "webpush";

        public Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, CancellationToken cancellationToken)
        {
            Sent.Add((subscription, message));
            return Task.FromResult(new PushSendResult(outcome, 201));
        }
    }

    private sealed record SubscriptionBody(string endpoint, object keys, string[]? kinds, bool? quietWhenDesk, string? channel = null, string? previousEndpoint = null);

    private static SubscriptionBody Body(string endpoint, string[]? kinds = null, bool? quiet = null)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        return new SubscriptionBody(
            endpoint,
            new { p256dh = Base64Url.Encode([0x04, .. p.Q.X!, .. p.Q.Y!]), auth = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)) },
            kinds,
            quiet);
    }

    private static HttpRequestMessage Delete(string endpoint) =>
        new(HttpMethod.Delete, "/api/push/subscriptions") { Content = JsonContent.Create(new { endpoint }) };

    private static IPushSubscriptionRepository Subscriptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IPushSubscriptionRepository>();

    private static Task<(DeviceSummary Device, string Token)> IssueAsync(WebApplicationFactory<Program> factory, string name = "Pixel") =>
        factory.Services.GetRequiredService<DeviceTokenService>().IssueAsync(name, "android");

    private static string MachineToken(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static ApiWebApplicationFactory CreateFactory(Action<IServiceCollection>? services = null) =>
        new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "0.0.0.0", configureTestServices: services);

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
