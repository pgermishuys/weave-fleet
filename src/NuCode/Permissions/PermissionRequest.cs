namespace NuCode.Permissions;

/// <summary>
/// Represents a pending permission request awaiting user decision.
/// </summary>
public sealed record PermissionRequest
{
    /// <summary>Gets the unique identifier for this request.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the session this request belongs to.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Gets the permission type (e.g., "bash", "edit").</summary>
    public required string Permission { get; init; }

    /// <summary>Gets the patterns being accessed.</summary>
    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary>Gets the patterns to use for "always allow" rules.</summary>
    public required IReadOnlyList<string> AlwaysPatterns { get; init; }

    /// <summary>Gets optional metadata about the request.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
