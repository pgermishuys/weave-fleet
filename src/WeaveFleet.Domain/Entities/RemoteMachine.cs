namespace WeaveFleet.Domain.Entities;

/// <summary>Another Fleet this one knows: where it is, and its access token (encrypted).</summary>
public sealed record RemoteMachine
{
    /// <summary>The machine's own id, from its <c>GET /api/machine</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Base URL without a trailing slash, e.g. <c>https://falcon.tail9c2e.ts.net</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Its access token, encrypted with Data Protection. Never sent to a paired device.</summary>
    public required string EncryptedToken { get; init; }

    public string? Os { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>One of <see cref="RemoteMachineStatuses"/>.</summary>
    public string Status { get; init; } = RemoteMachineStatuses.Unknown;
}

public static class RemoteMachineStatuses
{
    public const string Unknown = "unknown";
    public const string Online = "online";
    public const string Unreachable = "unreachable";

    /// <summary>It turned the token away: someone replaced it there. Not retried until the token changes.</summary>
    public const string Unauthorized = "unauthorized";
}

/// <summary>A device token another machine made for one of this machine's paired devices.</summary>
public sealed record DeviceGrant
{
    /// <summary>The paired device here.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The other machine.</summary>
    public required string MachineId { get; init; }

    /// <summary>The other machine's id for the device it made.</summary>
    public required string RemoteDeviceId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set when the device was removed here but not yet there.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}
