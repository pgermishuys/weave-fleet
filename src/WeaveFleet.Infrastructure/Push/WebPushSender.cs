using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Push;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Infrastructure.Push;

/// <summary>
/// Sends Web Push (<c>webpush</c> channel): the payload encrypted as <c>aes128gcm</c> for the subscription's keys,
/// signed with this machine's VAPID key, POSTed to the subscription's endpoint. Endpoints are capability URLs, so
/// only their host is ever logged.
/// </summary>
public sealed partial class WebPushSender(
    IHttpClientFactory httpClients,
    VapidKeyStore vapidKeys,
    FleetOptions options,
    TimeProvider time,
    ILogger<WebPushSender> logger) : IPushSender
{
    public const string HttpClientName = "WebPush";

    public string Channel => "webpush";

    public async Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            return new PushSendResult(PushSendOutcome.Gone);

        byte[] body;
        try
        {
            body = WebPushEncryption.Encrypt(
                Encoding.UTF8.GetBytes(message.Payload),
                Base64Url.Decode(subscription.P256dh),
                Base64Url.Decode(subscription.Auth));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // Keys a browser would never send: the subscription can't be used.
            LogBadKeys(logger, endpoint.Host);
            return new PushSendResult(PushSendOutcome.Gone);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", VapidAuthorization.Create(endpoint, options.Push.Subject, vapidKeys.Get(), time.GetUtcNow()));
        request.Headers.TryAddWithoutValidation("TTL", ((int)message.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", message.Urgent ? "high" : "normal");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var result = PushSendResult.FromStatus((int)response.StatusCode);
            if (result.Outcome != PushSendOutcome.Delivered)
                LogRefused(logger, endpoint.Host, (int)response.StatusCode);
            return result;
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, endpoint.Host, ex.Message);
            return new PushSendResult(PushSendOutcome.RetryLater);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, endpoint.Host, "timed out");
            return new PushSendResult(PushSendOutcome.RetryLater);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push to {Host} was refused with {Status}.")]
    private static partial void LogRefused(ILogger logger, string host, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach the push service at {Host}: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A push subscription at {Host} has keys that can't be used.")]
    private static partial void LogBadKeys(ILogger logger, string host);
}
