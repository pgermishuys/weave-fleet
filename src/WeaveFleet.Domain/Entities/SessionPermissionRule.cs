namespace WeaveFleet.Domain.Entities;

/// <summary>
/// One "Don't ask again" the user clicked in a session: its tool on the patterns the ask named. One click can allow
/// several patterns, so a rule holds them all and is listed and removed as one.
/// </summary>
public sealed record SessionPermissionRule
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    /// <summary>What kind of thing it allows (<c>PermissionKinds</c>): shell, edit, web or other.</summary>
    public required string Kind { get; init; }

    /// <summary>The tool the ask was for, as the harness named it.</summary>
    public required string Tool { get; init; }

    /// <summary>The patterns allowed; <c>*</c> alone is every use of the tool.</summary>
    public required IReadOnlyList<string> Patterns { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
