using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Push;

/// <summary>
/// Sends a push over one channel. <see cref="Channel"/> matches <see cref="PushSubscriptionRecord.Channel"/>:
/// <c>webpush</c> now; a native app's gateway (<c>apns</c>, <c>fcm</c>) can plug in later beside it.
/// </summary>
public interface IPushSender
{
    string Channel { get; }

    Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>A push to send: the JSON payload (≤ 1 KB), how urgent it is, and how long a push service may hold it.</summary>
public sealed record PushMessage(string Payload, bool Urgent, TimeSpan TimeToLive)
{
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);
}

public enum PushSendOutcome
{
    Delivered,

    /// <summary>The subscription is gone (404/410): delete it.</summary>
    Gone,

    /// <summary>The push service is busy or down (429/5xx): try later.</summary>
    RetryLater,

    Failed,
}

public sealed record PushSendResult(PushSendOutcome Outcome, int? StatusCode = null)
{
    public static PushSendResult FromStatus(int status) => status switch
    {
        >= 200 and < 300 => new(PushSendOutcome.Delivered, status),
        404 or 410 => new(PushSendOutcome.Gone, status),
        429 or >= 500 => new(PushSendOutcome.RetryLater, status),
        _ => new(PushSendOutcome.Failed, status),
    };
}
