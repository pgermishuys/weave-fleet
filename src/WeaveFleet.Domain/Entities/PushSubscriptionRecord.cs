namespace WeaveFleet.Domain.Entities;

/// <summary>
/// Somewhere this machine sends notifications: a browser's Web Push subscription today, a native app's token later
/// (<see cref="Channel"/>). The endpoint is a capability URL, so it's treated as a secret: never logged in full.
/// </summary>
public sealed record PushSubscriptionRecord
{
    public required string Id { get; init; }

    /// <summary>The paired device that subscribed; null for a browser signed in as the owner.</summary>
    public string? DeviceId { get; init; }

    /// <summary>How pushes reach it: <c>webpush</c> now; <c>apns</c> or <c>fcm</c> later.</summary>
    public string Channel { get; init; } = "webpush";

    public required string Endpoint { get; init; }

    /// <summary>The browser's P-256 public key, base64url.</summary>
    public required string P256dh { get; init; }

    /// <summary>The browser's 16-byte auth secret, base64url.</summary>
    public required string Auth { get; init; }

    /// <summary>Which notifications it wants: values of <c>SessionNotificationKinds</c>.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Skip pushes while Fleet is open on a computer screen.</summary>
    public bool QuietWhenDesk { get; init; }

    public string? UserAgent { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Failed sends in a row; reset by a delivered one.</summary>
    public int FailureCount { get; init; }
}
