using System.Text;

namespace NuCode.Utilities;

/// <summary>
/// Truncates tool output to configurable limits on lines and bytes.
/// </summary>
public static class OutputTruncator
{
    /// <summary>
    /// Default maximum number of lines before truncation.
    /// </summary>
    public const int DefaultMaxLines = 2000;

    /// <summary>
    /// Default maximum number of bytes before truncation (50 KB).
    /// </summary>
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>
    /// The result of a truncation operation.
    /// </summary>
    /// <param name="Content">The (possibly truncated) content.</param>
    /// <param name="Truncated">Whether truncation was applied.</param>
    public sealed record Result(string Content, bool Truncated);

    /// <summary>
    /// Truncates <paramref name="text"/> when it exceeds the configured limits.
    /// Returns the text unchanged when it fits within both limits.
    /// </summary>
    /// <param name="text">The text to potentially truncate.</param>
    /// <param name="maxLines">Maximum number of lines to keep. Defaults to <see cref="DefaultMaxLines"/>.</param>
    /// <param name="maxBytes">Maximum number of bytes to keep. Defaults to <see cref="DefaultMaxBytes"/>.</param>
    public static Result Truncate(string text, int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        var lines = text.Split('\n');
        var totalBytes = Encoding.UTF8.GetByteCount(text);

        if (lines.Length <= maxLines && totalBytes <= maxBytes)
        {
            return new Result(text, Truncated: false);
        }

        var kept = new List<string>();
        var bytes = 0;
        var hitBytes = false;

        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (i > 0 ? 1 : 0); // +1 for \n separator
            if (bytes + lineBytes > maxBytes)
            {
                hitBytes = true;
                break;
            }

            kept.Add(lines[i]);
            bytes += lineBytes;
        }

        var removed = hitBytes ? totalBytes - bytes : lines.Length - kept.Count;
        var unit = hitBytes ? "bytes" : "lines";
        var preview = string.Join('\n', kept);

        var truncated = $"{preview}\n\n...{removed} {unit} truncated...";
        return new Result(truncated, Truncated: true);
    }
}
