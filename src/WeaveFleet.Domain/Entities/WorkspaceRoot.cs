namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A root directory the user has added for scanning repositories.
/// </summary>
public sealed class WorkspaceRoot
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
