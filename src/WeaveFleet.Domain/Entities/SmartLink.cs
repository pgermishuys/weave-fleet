namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A URL detected in a session message, enriched with live status from an external provider.
/// </summary>
public sealed class SmartLink
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public bool IsDismissed { get; set; }
    public bool IsTerminal { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
}
