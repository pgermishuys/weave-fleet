using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Push;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Push;

namespace WeaveFleet.Infrastructure.Tests.Push;

/// <summary>
/// Sending to a push service, against a fake one that records what arrives: the aes128gcm body, the VAPID
/// Authorization header, TTL and Urgency, and how its answers map to outcomes.
/// </summary>
public sealed class WebPushSenderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fleet-push-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_push_arrives_encrypted_and_signed()
    {
        var service = new FakePushService(HttpStatusCode.Created);
        var (sender, keys) = CreateSender(service);
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscription = Subscription(browser);

        var result = await sender.SendAsync(subscription, new PushMessage("""{"v":1}""", Urgent: true, TimeSpan.FromHours(1)), CancellationToken.None);

        result.Outcome.ShouldBe(PushSendOutcome.Delivered);
        var request = service.Requests.Single();
        request.Uri.ShouldBe(new Uri(subscription.Endpoint));
        request.ContentEncoding.ShouldBe("aes128gcm");
        request.ContentType.ShouldBe("application/octet-stream");
        request.Ttl.ShouldBe("3600");
        request.Urgency.ShouldBe("high");
        request.Body.Length.ShouldBeGreaterThan(86);

        // Authorization: vapid t=<jwt>, k=<public key>; the JWT is ES256 over the push service's origin.
        request.Authorization.ShouldStartWith("vapid t=");
        var parts = request.Authorization["vapid ".Length..].Split(", ");
        var jwt = parts[0]["t=".Length..];
        parts[1].ShouldBe($"k={keys.PublicKey}");
        var segments = jwt.Split('.');
        var claims = JsonDocument.Parse(Base64Url.Decode(segments[1])).RootElement;
        claims.GetProperty("aud").GetString().ShouldBe("https://fcm.googleapis.com");
        claims.GetProperty("sub").GetString().ShouldBe("https://tryweave.io");
        claims.GetProperty("exp").GetInt64().ShouldBe((_time.GetUtcNow() + VapidAuthorization.Lifetime).ToUnixTimeSeconds());

        var publicKey = Base64Url.Decode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            Base64Url.Decode(segments[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();
    }

    [Fact]
    public async Task Ordinary_notifications_are_normal_urgency()
    {
        var service = new FakePushService(HttpStatusCode.Created);
        var (sender, _) = CreateSender(service);
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        await sender.SendAsync(Subscription(browser), new PushMessage("{}", Urgent: false, TimeSpan.FromMinutes(5)), CancellationToken.None);

        service.Requests.Single().Urgency.ShouldBe("normal");
        service.Requests.Single().Ttl.ShouldBe("300");
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, PushSendOutcome.Gone)]
    [InlineData(HttpStatusCode.NotFound, PushSendOutcome.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushSendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadGateway, PushSendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, PushSendOutcome.Failed)]
    [InlineData(HttpStatusCode.Forbidden, PushSendOutcome.Failed)]
    public async Task The_push_service_answer_decides_the_outcome(HttpStatusCode status, PushSendOutcome expected)
    {
        var (sender, _) = CreateSender(new FakePushService(status));
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var result = await sender.SendAsync(Subscription(browser), new PushMessage("{}", false, TimeSpan.FromHours(1)), CancellationToken.None);

        result.Outcome.ShouldBe(expected);
        result.StatusCode.ShouldBe((int)status);
    }

    [Fact]
    public async Task An_unusable_subscription_is_gone_without_a_request()
    {
        var service = new FakePushService(HttpStatusCode.Created);
        var (sender, _) = CreateSender(service);
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        (await sender.SendAsync(Subscription(browser) with { Endpoint = "http://insecure.example/push" }, new PushMessage("{}", false, TimeSpan.FromHours(1)), CancellationToken.None))
            .Outcome.ShouldBe(PushSendOutcome.Gone);
        (await sender.SendAsync(Subscription(browser) with { P256dh = "AAAA" }, new PushMessage("{}", false, TimeSpan.FromHours(1)), CancellationToken.None))
            .Outcome.ShouldBe(PushSendOutcome.Gone);
        service.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_push_service_is_retried_later()
    {
        var (sender, _) = CreateSender(new FakePushService(throws: true));
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        (await sender.SendAsync(Subscription(browser), new PushMessage("{}", false, TimeSpan.FromHours(1)), CancellationToken.None))
            .Outcome.ShouldBe(PushSendOutcome.RetryLater);
    }

    private (WebPushSender Sender, VapidKeys Keys) CreateSender(FakePushService service)
    {
        var store = new VapidKeyStore(Path.Combine(_dir, "fleet.db"));
        var sender = new WebPushSender(new SingleClientFactory(service), store, new FleetOptions(), _time, NullLogger<WebPushSender>.Instance);
        return (sender, store.Get());
    }

    private static PushSubscriptionRecord Subscription(ECDiffieHellman browser)
    {
        var p = browser.ExportParameters(false);
        return new PushSubscriptionRecord
        {
            Id = "sub-1",
            Endpoint = "https://fcm.googleapis.com/fcm/send/abc123",
            P256dh = Base64Url.Encode([0x04, .. p.Q.X!, .. p.Q.Y!]),
            Auth = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            Kinds = ["permission"],
        };
    }

    private sealed record CapturedRequest(Uri Uri, string Authorization, string? Ttl, string? Urgency, string? ContentEncoding, string? ContentType, byte[] Body);

    private sealed class FakePushService(HttpStatusCode status = HttpStatusCode.Created, bool throws = false) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throws)
                throw new HttpRequestException("connection refused");

            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.GetValues("Authorization").Single(),
                request.Headers.TryGetValues("TTL", out var ttl) ? ttl.Single() : null,
                request.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : null,
                request.Content?.Headers.ContentEncoding.SingleOrDefault(),
                request.Content?.Headers.ContentType?.MediaType,
                await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
