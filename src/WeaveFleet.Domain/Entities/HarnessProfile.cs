namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A named set of harness config the user picks when starting a session. Fleet doesn't read the content; the
/// harness decides what it means (for OpenCode, an opencode.json layered over the user's own).
/// </summary>
public sealed class HarnessProfile
{
    public string Id { get; set; } = string.Empty;
    public string HarnessType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    /// <summary>New sessions on this harness start with the default profile unless they pick another.</summary>
    public bool IsDefault { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
}
