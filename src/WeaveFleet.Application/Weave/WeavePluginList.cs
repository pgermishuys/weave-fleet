using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WeaveFleet.Application.Weave;

/// <summary>
/// Reads and edits the plugin list in a harness's JSON config (<c>opencode.json</c> or <c>opencode.jsonc</c>) as text,
/// so the user's comments, key order and formatting stay as they were. Only the list's entries change.
/// </summary>
public static class WeavePluginList
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The string entries in the root object's <paramref name="key"/> list; empty when there's no list.</summary>
    /// <exception cref="FormatException">The text isn't a JSON object, or <paramref name="key"/> isn't a list.</exception>
    public static IReadOnlyList<string> Read(string text, string key)
        => Scan(Encoding.UTF8.GetBytes(StripBom(text)), key).List?.Entries.Select(entry => entry.Value).OfType<string>().ToList() ?? [];

    /// <summary>
    /// <paramref name="text"/> with <paramref name="entry"/> at the end of the root object's <paramref name="key"/> list.
    /// The list is added as the object's first key when there's none.
    /// </summary>
    /// <exception cref="FormatException">The text isn't a JSON object, or <paramref name="key"/> isn't a list.</exception>
    public static string Add(string text, string key, string entry)
    {
        var bom = text.StartsWith('﻿');
        var source = StripBom(text);
        var bytes = Encoding.UTF8.GetBytes(source);
        var scan = Scan(bytes, key);
        var quoted = Quote(entry);

        string edited;
        if (scan.List is { } list)
        {
            if (list.Entries.Count == 0)
            {
                edited = Splice(bytes, list.Open + 1, list.Open + 1, quoted);
            }
            else
            {
                var last = list.Entries[^1];
                var multiline = Between(bytes, list.Open + 1, list.Entries[0].Start).Contains('\n');
                var separator = multiline ? $",\n{IndentOf(bytes, list.Entries[0].Start)}" : ", ";
                edited = Splice(bytes, last.End, last.End, separator + quoted);
            }
        }
        else if (scan.FirstProperty is { } first)
        {
            var multiline = Between(bytes, scan.RootOpen + 1, first).Contains('\n');
            var property = $"{Quote(key)}: [{quoted}],";
            edited = multiline
                ? Splice(bytes, scan.RootOpen + 1, scan.RootOpen + 1, $"\n{IndentOf(bytes, first)}{property}")
                : Splice(bytes, scan.RootOpen + 1, scan.RootOpen + 1, property + " ");
        }
        else
        {
            edited = Splice(bytes, scan.RootOpen, scan.RootClose + 1, $"{{\n  {Quote(key)}: [{quoted}]\n}}");
        }

        return bom ? '﻿' + edited : edited;
    }

    /// <summary>
    /// <paramref name="text"/> without <paramref name="entry"/> in the root object's <paramref name="key"/> list, or
    /// <see langword="null"/> when the list doesn't have it. The list stays, even when it's left empty.
    /// </summary>
    /// <exception cref="FormatException">The text isn't a JSON object, or <paramref name="key"/> isn't a list.</exception>
    public static string? Remove(string text, string key, string entry)
    {
        var bom = text.StartsWith('﻿');
        var source = StripBom(text);
        var bytes = Encoding.UTF8.GetBytes(source);
        if (Scan(bytes, key).List is not { } list)
            return null;

        var index = list.Entries.FindIndex(e => e.Value == entry);
        if (index < 0)
            return null;

        var target = list.Entries[index];
        var edited = index + 1 < list.Entries.Count
            ? Splice(bytes, target.Start, list.Entries[index + 1].Start, string.Empty)
            : index > 0
                ? Splice(bytes, list.Entries[index - 1].End, target.End, string.Empty)
                : Splice(bytes, target.Start, target.End, string.Empty);
        return bom ? '﻿' + edited : edited;
    }

    private sealed record Element(int Start, int End, string? Value);

    private sealed record ListSpan(int Open, List<Element> Entries);

    private sealed record ScanResult(int RootOpen, int RootClose, int? FirstProperty, ListSpan? List);

    /// <summary>Where the root object, its first key and the <paramref name="key"/> list's entries are, in bytes.</summary>
    private static ScanResult Scan(byte[] bytes, string key)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new FormatException("The config isn't a JSON object.");

            var rootOpen = (int)reader.TokenStartIndex;
            int? firstProperty = null;
            ListSpan? list = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    var rootClose = (int)reader.TokenStartIndex;
                    return new ScanResult(rootOpen, rootClose, firstProperty, list);
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;

                firstProperty ??= (int)reader.TokenStartIndex;
                if (!reader.ValueTextEquals(key))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new FormatException($"\"{key}\" in the config isn't a list.");

                var open = (int)reader.TokenStartIndex;
                var entries = new List<Element>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var start = (int)reader.TokenStartIndex;
                    var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip();
                    entries.Add(new Element(start, (int)reader.BytesConsumed, value));
                }

                // A duplicate key: OpenCode reads the last one, so that's the one to edit.
                list = new ListSpan(open, entries);
            }

            throw new FormatException("The config ends before its closing brace.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The config isn't valid JSON: {ex.Message}", ex);
        }
    }

    private static string Splice(byte[] bytes, int from, int to, string insert)
        => Encoding.UTF8.GetString(bytes, 0, from) + insert + Encoding.UTF8.GetString(bytes, to, bytes.Length - to);

    private static string Between(byte[] bytes, int from, int to) => Encoding.UTF8.GetString(bytes, from, to - from);

    /// <summary>The spaces and tabs that start the line <paramref name="position"/> is on.</summary>
    private static string IndentOf(byte[] bytes, int position)
    {
        var lineStart = position;
        while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n')
            lineStart--;

        var end = lineStart;
        while (end < position && bytes[end] is (byte)' ' or (byte)'\t')
            end++;
        return Encoding.UTF8.GetString(bytes, lineStart, end - lineStart);
    }

    private static string Quote(string value) => $"\"{JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";

    private static string StripBom(string text) => text.StartsWith('﻿') ? text[1..] : text;
}
