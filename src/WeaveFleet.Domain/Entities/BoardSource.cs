namespace WeaveFleet.Domain.Entities;

public sealed class BoardSource
{
    public string Id { get; set; } = string.Empty;
    public string BoardId { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string Config { get; set; } = string.Empty;
    public string? LastSyncAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
