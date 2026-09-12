namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A canvas in a session's right panel that both the user and the agent can change.
/// Every accepted change bumps <see cref="Version"/> and is recorded as a <see cref="CanvasRevision"/>.
/// </summary>
public sealed class Canvas
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
    /// <summary>Canvas kind, e.g. <c>diagram</c> or <c>sequence</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
    public int Version { get; set; }
    /// <summary>
    /// The last version the agent read or wrote. Used to find what the user removed since the agent last looked.
    /// </summary>
    public int AgentSeenVersion { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? ClosedAt { get; set; }
}
