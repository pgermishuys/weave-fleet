namespace WeaveFleet.Api.Contracts;

/// <summary>This machine's VAPID public key, for <c>pushManager.subscribe({ applicationServerKey })</c>.</summary>
public sealed record VapidKeyResponse(string PublicKey);

public sealed record PushKeys(string? P256dh, string? Auth);

/// <summary>
/// Registers (or updates) where to push. <paramref name="Kinds"/> left out keeps what the subscription had, or
/// takes them from <paramref name="PreviousEndpoint"/> (a subscription the browser rotated), or defaults to all.
/// </summary>
public sealed record SavePushSubscriptionRequest(
    string? Endpoint,
    PushKeys? Keys,
    IReadOnlyList<string>? Kinds = null,
    bool? QuietWhenDesk = null,
    string? Channel = null,
    string? PreviousEndpoint = null);

/// <summary>Names a subscription by its endpoint, which is kept out of URLs.</summary>
public sealed record PushEndpointRequest(string? Endpoint);

/// <summary>A subscription's settings, as the phone's setup screen shows them. Never the keys.</summary>
public sealed record PushSubscriptionResponse(
    string Channel,
    IReadOnlyList<string> Kinds,
    bool QuietWhenDesk,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSuccessAt);

/// <summary>How a test push went: <c>delivered</c>, <c>gone</c>, <c>retry_later</c> or <c>failed</c>.</summary>
public sealed record PushTestResponse(string Outcome, int? StatusCode);
