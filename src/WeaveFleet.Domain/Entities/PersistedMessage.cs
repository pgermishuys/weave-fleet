namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A message persisted to the Fleet SQLite database.
/// Distinct from <c>HarnessMessage</c> (harness-layer record) — this is a
/// persistence-layer entity with Dapper-friendly properties.
/// </summary>
public sealed class PersistedMessage
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PartsJson { get; set; } = "[]";
    public string Timestamp { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? AgentName { get; set; }
}
