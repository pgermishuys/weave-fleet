namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A running opencode harness instance with its associated port and process info.
/// </summary>
public sealed class Instance
{
    public string Id { get; set; } = string.Empty;
    public int Port { get; set; }
    public int? Pid { get; set; }
    public string Directory { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
    public string CreatedAt { get; set; } = string.Empty;
    public string? StoppedAt { get; set; }
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
}
