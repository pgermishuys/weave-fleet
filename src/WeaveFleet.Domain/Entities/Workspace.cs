namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A workspace directory, optionally isolated via worktree or clone strategy.
/// </summary>
public sealed class Workspace
{
    public string Id { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string? SourceDirectory { get; set; }
    public string IsolationStrategy { get; set; } = "existing";
    public string? Branch { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? CleanedUpAt { get; set; }
    public string? DisplayName { get; set; }
}
