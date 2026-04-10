namespace WeaveFleet.Domain.Entities;

public sealed class SessionSourceUsage
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? WorkspaceId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ResourceUrl { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
