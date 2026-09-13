using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// Sets and removes one property in a JSON-with-comments document by splicing text, so the
/// user's comments and formatting survive. OpenCode's config files are JSONC.
/// </summary>
internal static class JsoncEditor
{
    private const int IndentUnit = 2;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Allow,
        AllowTrailingCommas = true
    };

    // A config file people read and edit: keep '&', '+', '<' and non-ASCII as they are, not \u escapes.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Sets the property at <paramref name="path"/> (e.g. <c>["mcp", "github"]</c>) to <paramref name="value"/>,
    /// creating parent objects as needed. An empty document becomes <c>{}</c> first.
    /// </summary>
    /// <exception cref="JsonException">The document isn't valid JSONC.</exception>
    /// <exception cref="InvalidOperationException">The root, or a parent on the path, isn't an object.</exception>
    public static string SetProperty(string text, IReadOnlyList<string> path, JsonNode value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);

        if (string.IsNullOrWhiteSpace(text))
            text = "{\n}\n";

        var document = Document.Parse(text);
        var obj = document.Root;

        for (var i = 0; i < path.Count - 1; i++)
        {
            var parent = obj.Properties.FirstOrDefault(p => p.Name == path[i]);
            if (parent is null)
            {
                var nested = new JsonObject();
                var innermost = nested;
                for (var j = i + 1; j < path.Count - 1; j++)
                {
                    var next = new JsonObject();
                    innermost[path[j]] = next;
                    innermost = next;
                }
                innermost[path[^1]] = value.DeepClone();
                return document.Insert(obj, path[i], nested);
            }

            obj = parent.Value as Node.Object
                  ?? throw new InvalidOperationException($"'{string.Join('.', path.Take(i + 1))}' isn't a JSON object.");
        }

        var existing = obj.Properties.FirstOrDefault(p => p.Name == path[^1]);
        return existing is null
            ? document.Insert(obj, path[^1], value)
            : document.Replace(existing, value);
    }

    /// <summary>
    /// Removes the property at <paramref name="path"/> and its comma. Returns the text unchanged,
    /// and <c>false</c>, when the property isn't there.
    /// </summary>
    /// <exception cref="JsonException">The document isn't valid JSONC.</exception>
    public static (string Text, bool Removed) RemoveProperty(string text, IReadOnlyList<string> path)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);

        if (string.IsNullOrWhiteSpace(text))
            return (text, false);

        var document = Document.Parse(text);
        var obj = document.Root;

        for (var i = 0; i < path.Count - 1; i++)
        {
            if (obj.Properties.FirstOrDefault(p => p.Name == path[i])?.Value is not Node.Object next)
                return (text, false);
            obj = next;
        }

        var property = obj.Properties.FirstOrDefault(p => p.Name == path[^1]);
        return property is null ? (text, false) : (document.Remove(obj, property), true);
    }

    private abstract record Node(int Start, int End)
    {
        public sealed record Object(int Start, int End, List<Property> Properties) : Node(Start, End);

        public sealed record Other(int Start, int End) : Node(Start, End);
    }

    private sealed record Property(string Name, int NameStart, Node Value);

    /// <summary>A parsed document: the root object, and where each comment sits (byte offsets).</summary>
    private sealed class Document
    {
        private readonly byte[] _bytes;
        private readonly bool _hasBom;
        private readonly string _newline;
        private readonly List<(int Start, int End)> _comments;

        private Document(byte[] bytes, bool hasBom, string newline, Node.Object root, List<(int Start, int End)> comments)
        {
            _bytes = bytes;
            _hasBom = hasBom;
            _newline = newline;
            Root = root;
            _comments = comments;
        }

        public Node.Object Root { get; }

        public static Document Parse(string text)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            var hasBom = encoded.AsSpan().StartsWith(Bom);
            var bytes = hasBom ? encoded[Bom.Length..] : encoded;

            var comments = new List<(int Start, int End)>();
            var reader = new Utf8JsonReader(bytes, ReaderOptions);

            if (!Next(ref reader, comments))
                throw new JsonException("The document is empty.");

            var root = ReadValue(ref reader, comments) as Node.Object
                       ?? throw new InvalidOperationException("The document isn't a JSON object.");

            // Anything after the root other than comments is invalid; the reader reports it.
            while (Next(ref reader, comments))
            {
            }

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return new Document(bytes, hasBom, newline, root, comments);
        }

        public string Replace(Property property, JsonNode value)
        {
            var indent = LineIndent(property.NameStart);
            return Splice(property.Value.Start, property.Value.End, Render(value, indent));
        }

        public string Insert(Node.Object obj, string name, JsonNode value)
        {
            if (obj.Properties.Count > 0)
            {
                // Right after the last value: an existing trailing comma then trails the new property.
                var last = obj.Properties[^1];
                var indent = LineIndent(last.NameStart);
                return Splice(last.Value.End, last.Value.End, $",\n{indent}{RenderProperty(name, value, indent)}");
            }

            var outer = LineIndent(obj.Start);
            var inner = outer + new string(' ', IndentUnit);
            return Splice(obj.Start + 1, obj.Start + 1, $"\n{inner}{RenderProperty(name, value, inner)}\n{outer}");
        }

        public string Remove(Node.Object obj, Property property)
        {
            var start = property.NameStart;
            var end = property.Value.End;
            int? precedingComma = null;

            var after = SkipTrivia(end, forward: true);
            if (after < _bytes.Length && _bytes[after] == (byte)',')
            {
                end = after + 1;
            }
            else if (obj.Properties.IndexOf(property) > 0)
            {
                // The last property: drop the comma after the one before it, but not the comments in between.
                var before = SkipTrivia(start, forward: false);
                if (before > 0 && _bytes[before - 1] == (byte)',')
                    precedingComma = before - 1;
            }

            // Take the whole line when the property had it to itself, so no blank line is left.
            var lineStart = LineStart(start);
            var lineEnd = LineEnd(end);
            if (IsBlank(lineStart, start) && IsBlank(end, lineEnd))
            {
                start = lineStart;
                end = lineEnd < _bytes.Length ? lineEnd + 1 : lineEnd;
            }

            return precedingComma is { } comma
                ? Splice([(comma, comma + 1, string.Empty), (start, end, string.Empty)])
                : Splice(start, end, string.Empty);
        }

        private static bool Next(ref Utf8JsonReader reader, List<(int Start, int End)> comments)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.Comment)
                    return true;
                comments.Add(((int)reader.TokenStartIndex, (int)reader.BytesConsumed));
            }

            return false;
        }

        private static Node ReadValue(ref Utf8JsonReader reader, List<(int Start, int End)> comments)
        {
            var start = (int)reader.TokenStartIndex;

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    var properties = new List<Property>();
                    while (Next(ref reader, comments) && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var name = reader.GetString()!;
                        var nameStart = (int)reader.TokenStartIndex;
                        Next(ref reader, comments);
                        properties.Add(new Property(name, nameStart, ReadValue(ref reader, comments)));
                    }
                    return new Node.Object(start, (int)reader.BytesConsumed, properties);

                case JsonTokenType.StartArray:
                    while (Next(ref reader, comments) && reader.TokenType != JsonTokenType.EndArray)
                        ReadValue(ref reader, comments);
                    return new Node.Other(start, (int)reader.BytesConsumed);

                default:
                    return new Node.Other(start, (int)reader.BytesConsumed);
            }
        }

        private string Splice(int start, int end, string replacement) => Splice([(start, end, replacement)]);

        /// <summary>
        /// Replaces byte ranges, given in order and not overlapping. Replacements use \n; the file's own
        /// line endings go in.
        /// </summary>
        private string Splice(IReadOnlyList<(int Start, int End, string Replacement)> edits)
        {
            var result = new List<byte>(_bytes.Length + Bom.Length + edits.Sum(e => e.Replacement.Length));
            if (_hasBom)
                result.AddRange(Bom);

            var position = 0;
            foreach (var (start, end, replacement) in edits)
            {
                result.AddRange(_bytes.AsSpan(position, start - position));
                result.AddRange(Encoding.UTF8.GetBytes(replacement.Replace("\n", _newline, StringComparison.Ordinal)));
                position = end;
            }

            result.AddRange(_bytes.AsSpan(position));
            return Encoding.UTF8.GetString([.. result]);
        }

        /// <summary>Moves past whitespace and comments; backwards, returns the offset just after the last one skipped.</summary>
        private int SkipTrivia(int position, bool forward)
        {
            while (true)
            {
                if (forward)
                {
                    if (position >= _bytes.Length)
                        return position;
                    if (IsWhitespace(_bytes[position]))
                    {
                        position++;
                        continue;
                    }
                    var comment = _comments.FirstOrDefault(c => c.Start == position);
                    if (comment != default)
                    {
                        position = comment.End;
                        continue;
                    }
                    return position;
                }
                else
                {
                    if (position <= 0)
                        return position;
                    if (IsWhitespace(_bytes[position - 1]))
                    {
                        position--;
                        continue;
                    }
                    // A line comment's span can include its newline, which was just skipped as whitespace.
                    var comment = _comments.FirstOrDefault(c => c.Start < position && position <= c.End);
                    if (comment != default)
                    {
                        position = comment.Start;
                        continue;
                    }
                    return position;
                }
            }
        }

        private string LineIndent(int position)
        {
            var lineStart = LineStart(position);
            var end = lineStart;
            while (end < position && _bytes[end] is (byte)' ' or (byte)'\t')
                end++;
            return Encoding.UTF8.GetString(_bytes, lineStart, end - lineStart);
        }

        private int LineStart(int position)
        {
            while (position > 0 && _bytes[position - 1] != (byte)'\n')
                position--;
            return position;
        }

        private int LineEnd(int position)
        {
            while (position < _bytes.Length && _bytes[position] != (byte)'\n')
                position++;
            return position;
        }

        private bool IsBlank(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!IsWhitespace(_bytes[i]))
                    return false;
            }
            return true;
        }

        private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        private static string RenderProperty(string name, JsonNode value, string indent) =>
            $"{JsonValue.Create(name).ToJsonString(WriteOptions)}: {Render(value, indent)}";

        /// <summary>Indented JSON whose continuation lines line up under a property at <paramref name="indent"/>.</summary>
        private static string Render(JsonNode value, string indent) =>
            value.ToJsonString(WriteOptions).ReplaceLineEndings("\n").Replace("\n", "\n" + indent, StringComparison.Ordinal);
    }
}
