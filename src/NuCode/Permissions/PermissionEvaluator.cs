using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NuCode.Permissions;

/// <summary>
/// Evaluates permission rules using a last-match-wins strategy with wildcard pattern matching.
/// </summary>
internal static class PermissionEvaluator
{
    /// <summary>
    /// Evaluates the given permission and pattern against the provided rulesets.
    /// Uses a last-match-wins strategy: all rulesets are flattened and the last matching rule determines the action.
    /// If no rule matches, returns <see cref="PermissionAction.Ask"/>.
    /// </summary>
    public static PermissionAction Evaluate(string permission, string pattern, params PermissionRuleset[] rulesets)
    {
        PermissionAction result = PermissionAction.Ask;

        foreach (var ruleset in rulesets)
        {
            foreach (var rule in ruleset.Rules)
            {
                if (WildcardMatch(permission, rule.Permission) && WildcardMatch(pattern, rule.Pattern))
                {
                    result = rule.Action;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Matches a string against a wildcard pattern. Supports * (any chars) and ? (single char).
    /// Normalizes path separators. Case-insensitive on Windows, case-sensitive on Unix.
    /// </summary>
    internal static bool WildcardMatch(string input, string pattern)
    {
        // Normalize path separators
        var normalizedInput = input.Replace('\\', '/');
        var normalizedPattern = pattern.Replace('\\', '/');

        // Convert wildcard pattern to regex
        var regexPattern = WildcardToRegex(normalizedPattern);

        var options = RegexOptions.Singleline;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            return Regex.IsMatch(normalizedInput, regexPattern, options, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                // Escape regex special characters
                case '.':
                case '+':
                case '^':
                case '$':
                case '{':
                case '}':
                case '(':
                case ')':
                case '|':
                case '[':
                case ']':
                case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Special handling: if pattern ends with " .*" (space + wildcard),
        // make the trailing part optional so "ls *" matches both "ls" and "ls -la"
        var result = sb.ToString();
        if (result.EndsWith(" .*"))
        {
            result = string.Concat(result.AsSpan(0, result.Length - 3), "( .*)?");
        }

        return result + "$";
    }
}
