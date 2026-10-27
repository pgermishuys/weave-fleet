using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// The short "now" line for a session: what its agent is doing this second, in one line a tile can show. Only the
/// short target of a tool call is read (a command, a path, a pattern), never the content it writes.
/// </summary>
public static partial class NowSummary
{
    /// <summary>The longest a line gets, in characters.</summary>
    public const int MaxLength = 120;

    public const string Thinking = "Thinking";

    // The input fields that name what a tool works on, in the order they are tried.
    private static readonly string[] TargetFields = ["command", "pattern", "filePath", "file_path", "path", "url", "description", "query"];

    /// <summary>A tool call as <c>tool · target</c>, or just the tool when its input names no target.</summary>
    public static string FromTool(string toolName, JsonElement? input)
    {
        var tool = toolName.ToLowerInvariant();
        var target = input is { ValueKind: JsonValueKind.Object } fields ? TargetOf(fields) : null;
        return Shorten(target is null ? tool : $"{tool} · {target}");
    }

    /// <summary>The first line of a reply.</summary>
    public static string FromReply(string text) => Shorten(text);

    private static string? TargetOf(JsonElement input)
    {
        foreach (var field in TargetFields)
        {
            if (input.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && FirstLine(value.GetString()) is { Length: > 0 } line)
                return line;
        }

        return null;
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', 2)[0].Trim();

    private static string Shorten(string text)
    {
        var line = Mask(FirstLine(text) ?? "");
        return line.Length <= MaxLength ? line : string.Concat(line.AsSpan(0, MaxLength - 1), "…");
    }

    /// <summary>
    /// Hides the values a command line would give away: <c>NAME=value</c> where NAME has TOKEN, KEY, SECRET or
    /// PASSWORD in it, and whatever follows <c>Bearer</c> or <c>Basic</c>.
    /// </summary>
    internal static string Mask(string line) =>
        BearerOrBasic().Replace(SecretAssignment().Replace(line, "$1=***"), "$1 ***");

    [GeneratedRegex(@"\b([A-Za-z0-9_]*(?:TOKEN|KEY|SECRET|PASSWORD)[A-Za-z0-9_]*)=(""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerOrBasic();
}
