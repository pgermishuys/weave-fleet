namespace WeaveFleet.Domain.Entities;

public sealed class BoardLane
{
    public string Id { get; set; } = string.Empty;
    public string BoardId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public bool IsInbox { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
