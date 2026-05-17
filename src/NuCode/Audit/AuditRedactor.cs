using System.Text;
using System.Text.RegularExpressions;

namespace NuCode.Audit;

/// <summary>
/// Produces a redacted, truncated summary of tool arguments suitable for audit logs.
/// </summary>
internal static partial class AuditRedactor
{
    private const int MaxSummaryLength = 200;

    // Keys whose values must never appear in audit logs.
    [GeneratedRegex(@"\b(content|text|body|secret|token|password|key|authorization|credential|api_?key|private_?key|bearer|cookie|session)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeyPattern();

    /// <summary>
    /// Returns a redacted summary (max 200 chars) of the given tool arguments.
    /// Sensitive key values are replaced with <c>[redacted]</c>.
    /// For the "bash" tool only the "command" arg is included.
    /// </summary>
    public static string Summarize(string toolName, IDictionary<string, object?> args)
    {
        if (args.Count == 0)
        {
            return string.Empty;
        }

        if (string.Equals(toolName, "bash", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeBash(args);
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            var value = IsSensitiveKey(k) ? "[redacted]" : v?.ToString() ?? "null";
            sb.Append(k).Append('=').Append(value);
        }

        return Truncate(sb.ToString());
    }

    private static string SummarizeBash(IDictionary<string, object?> args)
    {
        if (args.TryGetValue("command", out var cmd))
        {
            return Truncate($"command={cmd}");
        }

        // Fallback: include only the first non-sensitive key.
        foreach (var (k, v) in args)
        {
            if (!IsSensitiveKey(k))
            {
                return Truncate($"{k}={v}");
            }
        }

        return string.Empty;
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyPattern().IsMatch(key);

    private static string Truncate(string value) =>
        value.Length <= MaxSummaryLength
            ? value
            : string.Concat(value.AsSpan(0, MaxSummaryLength - 3), "...");
}
