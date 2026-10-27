using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>One change the dreaming model proposed, as it wrote it. Nothing here is trusted: <see cref="DreamApplier"/> checks it.</summary>
public sealed record DreamProposal
{
    public string? Action { get; set; }
    public string? List { get; set; }
    public string? Text { get; set; }
    public string? Why { get; set; }
    public List<string> Sessions { get; set; } = [];
    public string? Quote { get; set; }
    public bool Corrected { get; set; }
    public string? Note { get; set; }
    public string? Keep { get; set; }
    public List<string> Remove { get; set; } = [];
}

/// <summary>Reads the list of changes out of the dreaming session's last message.</summary>
public static partial class DreamAnswer
{
    [GeneratedRegex(@"```[ \t]*(?:json)?[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fence();

    /// <summary>
    /// The proposals in the last fenced <c>json</c> block, or else the last top-level <c>{…}</c> that has a
    /// <c>changes</c> list; <see langword="null"/> when there's none. An empty list is an answer.
    /// </summary>
    public static List<DreamProposal>? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        foreach (var candidate in Fence().Matches(message).Select(m => m.Groups[1].Value).Reverse())
        {
            if (TryRead(candidate) is { } fenced)
                return fenced;
        }

        foreach (var candidate in TopLevelObjects(message).Reverse())
        {
            if (TryRead(candidate) is { } bare)
                return bare;
        }

        return null;
    }

    private static List<DreamProposal>? TryRead(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.Trim());
            if (document.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                return null;

            var proposals = new List<DreamProposal>();
            foreach (var change in changes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
            {
                proposals.Add(new DreamProposal
                {
                    Action = Str(change, "action"),
                    List = Str(change, "list"),
                    Text = Str(change, "text"),
                    Why = Str(change, "why"),
                    Sessions = Strings(change, "sessions"),
                    Quote = Str(change, "quote"),
                    Corrected = change.TryGetProperty("corrected", out var corrected) && corrected.ValueKind == JsonValueKind.True,
                    Note = Str(change, "note"),
                    Keep = Str(change, "keep"),
                    Remove = Strings(change, "remove"),
                });
            }

            return proposals;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

    private static IEnumerable<string> TopLevelObjects(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (inString)
            {
                if (c == '\\')
                    index++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"' && depth > 0)
                inString = true;
            else if (c == '{')
            {
                if (depth++ == 0)
                    start = index;
            }
            else if (c == '}' && depth > 0 && --depth == 0)
                yield return text[start..(index + 1)];
        }
    }
}
