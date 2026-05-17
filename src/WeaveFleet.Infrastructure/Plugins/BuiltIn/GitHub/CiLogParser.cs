using System.Text.RegularExpressions;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

/// <summary>
/// Parses and extracts relevant lines from GitHub Actions job logs.
/// </summary>
internal static partial class CiLogParser
{
    // GitHub Actions log lines begin with a timestamp like: 2024-01-01T00:00:00.0000000Z space
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z ")]
    private static partial Regex TimestampPrefix();

    // Patterns that indicate errors or failures
    private static readonly string[] ErrorMarkers =
    [
        "##[error]",
        "##[Error]",
        "Error:",
        "error:",
        "FAILED",
        "FAILURE",
        "Exception:",
        "Unhandled exception",
        "fatal:",
        "FATAL:",
    ];

    /// <summary>
    /// Extracts relevant log lines from raw GitHub Actions job log text.
    /// Strips timestamp prefixes, prioritizes error lines, and includes context.
    /// Falls back to the last <paramref name="maxLines"/> lines if no errors found.
    /// </summary>
    public static string ExtractRelevantLogLines(string rawLog, int maxLines = 200)
    {
        if (string.IsNullOrWhiteSpace(rawLog))
            return string.Empty;

        var lines = rawLog.Split('\n', StringSplitOptions.None);

        // Strip timestamp prefixes
        var stripped = lines
            .Select(l => TimestampPrefix().Replace(l.TrimEnd('\r'), string.Empty))
            .ToArray();

        // Find error line indices
        var errorIndices = new HashSet<int>();
        for (var i = 0; i < stripped.Length; i++)
        {
            var line = stripped[i];
            if (ErrorMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                // Include 5 lines before and after each error
                for (var j = Math.Max(0, i - 5); j <= Math.Min(stripped.Length - 1, i + 5); j++)
                    errorIndices.Add(j);
            }
        }

        string[] relevant;
        if (errorIndices.Count > 0)
        {
            // Return error context lines in order, with separators between non-contiguous blocks
            var sortedIndices = errorIndices.OrderBy(x => x).ToList();
            var resultLines = new List<string>();
            var prevIndex = -2;

            foreach (var idx in sortedIndices)
            {
                if (prevIndex >= 0 && idx > prevIndex + 1)
                    resultLines.Add("...");
                resultLines.Add(stripped[idx]);
                prevIndex = idx;
            }

            relevant = resultLines.ToArray();
        }
        else
        {
            // No explicit errors found — return the last N lines
            relevant = stripped.TakeLast(maxLines).ToArray();
        }

        // Truncate to maxLines
        if (relevant.Length > maxLines)
            relevant = relevant.TakeLast(maxLines).ToArray();

        return string.Join('\n', relevant);
    }
}
