using Microsoft.Extensions.AI;

namespace NuCode.Permissions;

/// <summary>
/// Extracts permission information from a tool invocation context.
/// Tools implement this to declare what permission type and patterns they require.
/// </summary>
public interface IPermissionPatternExtractor
{
    /// <summary>
    /// Gets the permission type for this tool (e.g., "bash", "edit", "read", "write").
    /// </summary>
    string Permission { get; }

    /// <summary>
    /// Extracts the patterns to check against permission rules from the function arguments.
    /// </summary>
    /// <param name="functionName">The name of the function being invoked.</param>
    /// <param name="arguments">The arguments passed to the function.</param>
    /// <returns>
    /// The permission extraction result containing patterns and always-allow patterns,
    /// or <c>null</c> if no permission check is needed for this invocation.
    /// </returns>
    PermissionPatternResult? ExtractPatterns(string functionName, AIFunctionArguments arguments);
}

/// <summary>
/// Result of extracting permission patterns from a tool invocation.
/// </summary>
/// <param name="Patterns">The patterns to check against permission rules.</param>
/// <param name="AlwaysPatterns">The patterns to add to session rules if "always allow" is chosen.</param>
public sealed record PermissionPatternResult(
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> AlwaysPatterns);
