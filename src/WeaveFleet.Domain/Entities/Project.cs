namespace WeaveFleet.Domain.Entities;

/// <summary>
/// An organizational grouping of sessions. The special "scratch" project is auto-created at startup.
/// </summary>
public sealed class Project
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>"user" | "scratch"</summary>
    public string Type { get; set; } = "user";
    public int Position { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
