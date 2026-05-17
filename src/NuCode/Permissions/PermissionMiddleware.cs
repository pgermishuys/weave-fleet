using Microsoft.Extensions.AI;

namespace NuCode.Permissions;

/// <summary>
/// Provides permission-checking middleware for the Microsoft Agent Framework function invocation pipeline.
/// Intercepts tool calls before execution and enforces permission rules.
/// </summary>
public static class PermissionMiddleware
{
    /// <summary>
    /// Known edit tool names that all map to the "edit" permission type.
    /// </summary>
    public static readonly string[] EditToolNames = ["edit", "write", "apply_patch", "multiedit"];

    /// <summary>
    /// Creates a function invocation middleware callback that checks permissions before each tool call.
    /// Use with <c>agent.AsBuilder().Use(PermissionMiddleware.Create(...))</c>.
    /// </summary>
    /// <param name="permissionService">The permission service for ask/reply flow.</param>
    /// <param name="sessionId">The current session ID.</param>
    /// <param name="rulesets">The permission rulesets to evaluate against.</param>
    /// <param name="extractors">Map of function name → pattern extractor.</param>
    /// <returns>A middleware callback compatible with the Agent Framework's Use() extension.</returns>
    public static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Create(
        IPermissionService permissionService,
        SessionId sessionId,
        IReadOnlyList<PermissionRuleset> rulesets,
        IReadOnlyDictionary<string, IPermissionPatternExtractor> extractors)
    {
        return async (context, cancellationToken) =>
        {
            var functionName = context.Function.Name;

            if (extractors.TryGetValue(functionName, out var extractor))
            {
                var result = extractor.ExtractPatterns(functionName, context.Arguments);
                if (result is not null)
                {
                    try
                    {
                        await permissionService.RequestPermissionAsync(
                            sessionId,
                            extractor.Permission,
                            result.Patterns,
                            result.AlwaysPatterns,
                            rulesets,
                            cancellationToken);
                    }
                    catch (PermissionDeniedException)
                    {
                        // Terminate the function invocation loop
                        context.Terminate = true;
                        return $"Permission denied: {extractor.Permission} for {string.Join(", ", result.Patterns)}";
                    }
                }
            }

            // Permission granted (or no extractor registered) — invoke the function
            return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        };
    }

    /// <summary>
    /// Resolves the canonical permission type for a tool name.
    /// Edit-family tools ("edit", "write", "apply_patch", "multiedit") all map to "edit".
    /// </summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The canonical permission type.</returns>
    public static string ResolvePermissionType(string toolName) =>
        Array.Exists(EditToolNames, name => string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase))
            ? "edit"
            : toolName;
}
