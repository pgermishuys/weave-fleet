namespace WeaveFleet.Domain.Entities;

public sealed class BoardCard
{
    public string Id { get; set; } = string.Empty;
    public string BoardId { get; set; } = string.Empty;
    public string LaneId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SourceType { get; set; }
    public string? SourceKey { get; set; }
    public string? Metadata { get; set; }
    public int Position { get; set; }
    public string? ArchivedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
