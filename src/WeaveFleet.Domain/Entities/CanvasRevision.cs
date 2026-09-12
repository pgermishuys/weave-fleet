namespace WeaveFleet.Domain.Entities;

/// <summary>
/// The change ops that took a canvas to <see cref="Version"/>, and who made them.
/// </summary>
public sealed class CanvasRevision
{
    public const string AgentActor = "agent";
    public const string UserActor = "user";

    public string CanvasId { get; set; } = string.Empty;
    public int Version { get; set; }
    /// <summary><see cref="AgentActor"/> or <see cref="UserActor"/>.</summary>
    public string Actor { get; set; } = string.Empty;
    public string OpsJson { get; set; } = "[]";
    public string CreatedAt { get; set; } = string.Empty;
}
