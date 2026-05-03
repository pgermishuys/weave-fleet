using System.Text.Json.Serialization;

namespace NuCode.Audit;

/// <summary>
/// A single tool invocation audit record written to the session audit log.
/// </summary>
internal sealed record AuditEntry(
    /// <summary>UTC timestamp when the entry was recorded.</summary>
    DateTimeOffset Timestamp,

    /// <summary>Identifier of the NuCode session.</summary>
    string SessionId,

    /// <summary>The tool name that was invoked.</summary>
    string ToolName,

    /// <summary>A redacted, truncated summary of the arguments (max 200 chars).</summary>
    string? ArgsSummary,

    /// <summary>Outcome: "completed", "error", or "timeout".</summary>
    string Status,

    /// <summary>Duration of the tool call in milliseconds.</summary>
    long DurationMs,

    /// <summary>Identifier of the sub-agent that invoked the tool, if known.</summary>
    string? AgentId,

    /// <summary>Additional detail such as an error message.</summary>
    string? Detail)
{
    /// <summary>Kind discriminator, always "tool".</summary>
    [JsonPropertyName("kind")]
    public string Kind => "tool";
}

/// <summary>
/// A single permission decision audit record written to the session audit log.
/// </summary>
internal sealed record AuditPermissionEntry(
    /// <summary>UTC timestamp when the entry was recorded.</summary>
    DateTimeOffset Timestamp,

    /// <summary>Identifier of the NuCode session.</summary>
    string SessionId,

    /// <summary>The permission type (e.g. "bash", "edit").</summary>
    string Permission,

    /// <summary>The patterns that were evaluated.</summary>
    IReadOnlyList<string> Patterns,

    /// <summary>The decision: "allow", "deny", or "ask".</summary>
    string Decision)
{
    /// <summary>Kind discriminator, always "permission".</summary>
    [JsonPropertyName("kind")]
    public string Kind => "permission";
}
