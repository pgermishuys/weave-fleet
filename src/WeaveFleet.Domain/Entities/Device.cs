namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A device with its own access token, such as a phone paired by QR code. Only a hash of the token's secret is kept.
/// </summary>
public sealed record Device
{
    /// <summary>A ULID; also the first part of the device's token.</summary>
    public required string Id { get; init; }

    /// <summary>What the device is called in Settings, e.g. "Pieter's iPhone".</summary>
    public required string Name { get; init; }

    /// <summary><c>ios</c>, <c>android</c> or <c>other</c>; null when unknown.</summary>
    public string? Platform { get; init; }

    /// <summary>SHA-256 of the token's secret.</summary>
    public required byte[] TokenHash { get; init; }

    /// <summary>The machine that asked for this token on the device's behalf; null when the device paired here.</summary>
    public string? PairedVia { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the device last used its token. The token expires 30 days after this.</summary>
    public DateTimeOffset LastUsedAt { get; init; }

    /// <summary>When the device was removed; null while it has access.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}
