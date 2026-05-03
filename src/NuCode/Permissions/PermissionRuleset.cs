using System.Collections.Immutable;

namespace NuCode.Permissions;

/// <summary>
/// A named, immutable collection of permission rules.
/// </summary>
public sealed record PermissionRuleset
{
    /// <summary>
    /// Gets the name of this ruleset (e.g., "default", "session", "config").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the rules in this ruleset, ordered by evaluation priority (last match wins).
    /// </summary>
    public ImmutableArray<PermissionRule> Rules { get; init; } = [];

    /// <summary>
    /// Creates a new ruleset with the specified rules appended.
    /// </summary>
    public PermissionRuleset WithRules(params PermissionRule[] additionalRules) =>
        this with { Rules = Rules.AddRange(additionalRules) };
}
