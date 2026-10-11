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

/// <summary>
/// Raised while Fleet installs the Bun mods run on, and when it finishes or fails. Sent on the <c>sessions</c> topic; the
/// install is machine-wide, so it reaches every user.
/// </summary>
public sealed record ModsRuntimeChanged : DomainEvent
{
    /// <summary>Gets the payload describing what changed.</summary>
    public required ModsRuntimePayload Payload { get; init; }
}

/// <summary>
/// Payload describing a change to the mod runtime.
/// </summary>
public sealed record ModsRuntimePayload
{
    /// <summary>Gets the install running now or the last one since Fleet started; null before the first.</summary>
    public ModsRuntimeJob? Job { get; init; }

    /// <summary>Gets why this was raised: <c>job</c> (progress or a phase change), or <c>installed</c> (it succeeded).</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Fleet installing the mod runtime.
/// </summary>
public sealed record ModsRuntimeJob
{
    /// <summary>Gets where the install stands: <c>downloading</c>, <c>verifying</c>, <c>extracting</c>, <c>succeeded</c> or <c>failed</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Gets the Bun version being installed.</summary>
    public required string Version { get; init; }

    /// <summary>Gets what is happening or what happened, in a sentence.</summary>
    public string? Message { get; init; }

    /// <summary>Gets why the install failed (<c>offline</c>, <c>blocked</c>, <c>stopped</c>, <c>checksum</c>, <c>no-build</c>, <c>cancelled</c>, <c>other</c>); null unless it failed.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets how much of the archive has downloaded.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Gets the archive's size, when known.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>Gets when the install started.</summary>
    public required DateTimeOffset StartedAt { get; init; }
}
