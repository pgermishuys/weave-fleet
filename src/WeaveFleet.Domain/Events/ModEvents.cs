namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a mod was kept, undone, turned on or off, failed three times, or the user started Fleet without mods.
/// Clients refetch <c>/api/mods</c>.
/// </summary>
public sealed record ModsChanged : DomainEvent
{
    /// <summary>
    /// Gets the payload describing what changed.
    /// </summary>
    public required ModsChangedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing a change to the user's mods.
/// </summary>
public sealed record ModsChangedPayload
{
    /// <summary>
    /// Gets the mod's name; null for a change that is about all of them (safe mode).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets what happened: <c>kept</c>, <c>version</c>, <c>undone</c>, <c>on</c>, <c>off</c>, <c>strikes</c>,
    /// <c>draft-off</c>, <c>draft-on</c> or <c>safe-mode</c>.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the session whose draft changed, or whose draft was kept.
    /// </summary>
    public string? SessionId { get; init; }
}
