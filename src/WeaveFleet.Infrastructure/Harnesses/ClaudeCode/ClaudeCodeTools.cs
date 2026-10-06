using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Claude Code's tool calls as Fleet shows them: under the names and with the inputs Fleet already shows OpenCode's
/// (<c>Bash</c> is <c>bash</c>, <c>file_path</c> is <c>filePath</c>, a subagent's <c>Agent</c> is <c>task</c>), so the
/// conversation shows what a call ran or touched, and an edit's diff. Claude Code's names stop here; what reads its
/// stream directly (permissions, running work) keeps using them.
/// </summary>
internal static class ClaudeCodeTools
{
    /// <summary>Claude Code's <c>AskUserQuestion</c>: Fleet answers it from its question card, not as a permission.</summary>
    internal const string AskUserQuestion = "AskUserQuestion";

    private static readonly Dictionary<string, string> FilePath = new(StringComparer.Ordinal) { ["file_path"] = "filePath" };

    private static readonly Dictionary<string, string> EditStrings = new(StringComparer.Ordinal)
    {
        ["file_path"] = "filePath",
        ["old_string"] = "oldString",
        ["new_string"] = "newString",
        ["replace_all"] = "replaceAll",
    };

    private static readonly Dictionary<string, string> None = new(StringComparer.Ordinal);

    /// <summary>
    /// Fleet's name for each Claude Code tool, the renames its input gets, and the renames for the objects in one of its
    /// lists (<c>MultiEdit</c>'s edits, <c>AskUserQuestion</c>'s questions). Older CLIs call the subagent tool <c>Task</c>.
    /// </summary>
    private static readonly Dictionary<string, (string Name, Dictionary<string, string> Renames, string? List, Dictionary<string, string>? ItemRenames)> Shapes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bash"] = ("bash", None, null, null),
            ["Read"] = ("read", FilePath, null, null),
            ["Write"] = ("write", FilePath, null, null),
            ["Edit"] = ("edit", EditStrings, null, null),
            ["MultiEdit"] = ("edit", FilePath, "edits", EditStrings),
            ["NotebookEdit"] = ("edit", new(StringComparer.Ordinal) { ["notebook_path"] = "filePath", ["new_source"] = "newSource" }, null, null),
            ["Glob"] = ("glob", None, null, null),
            ["Grep"] = ("grep", new(StringComparer.Ordinal) { ["glob"] = "include" }, null, null),
            ["WebFetch"] = ("webfetch", None, null, null),
            ["WebSearch"] = ("websearch", None, null, null),
            ["Agent"] = ("task", None, null, null),
            ["Task"] = ("task", None, null, null),
            ["Skill"] = ("skill", new(StringComparer.Ordinal) { ["skill"] = "name" }, null, null),
            ["TodoWrite"] = ("todowrite", None, null, null),
            [AskUserQuestion] = ("question", None, "questions", new(StringComparer.Ordinal) { ["multiSelect"] = "multiple" }),
        };

    /// <summary>The name Fleet shows the tool under; a tool Fleet has no name for keeps Claude Code's.</summary>
    internal static string Name(string claudeName)
        => Shapes.TryGetValue(claudeName, out var shape) ? shape.Name : claudeName;

    /// <summary>Whether the call is Claude Code's question tool.</summary>
    internal static bool IsQuestion(string? claudeName) => string.Equals(claudeName, AskUserQuestion, StringComparison.Ordinal);

    /// <summary>The call's input with Fleet's names for its fields; other fields, and other tools' input, stay as they are.</summary>
    internal static JsonElement Input(string claudeName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !Shapes.TryGetValue(claudeName, out var shape)
            || (shape.Renames.Count == 0 && shape.List is null))
        {
            return input;
        }

        return Write(json => Rename(json, input, shape.Renames, shape.List, shape.ItemRenames));
    }

    /// <summary>
    /// What a file tool changes, as a unified diff, which Fleet shows inline with its line counts: from the patch Claude
    /// Code reports once the call ran (<paramref name="result"/>, its <c>tool_use_result</c>), otherwise from what the call
    /// asked for, a hunk per edit. Null for a tool that doesn't change a file.
    /// </summary>
    internal static string? Diff(string claudeName, JsonElement input, JsonElement? result = null)
    {
        var tool = Shapes.TryGetValue(claudeName, out var shape) ? claudeName : null;
        if (tool is null || shape.Name is not ("edit" or "write") || input.ValueKind != JsonValueKind.Object)
            return null;

        var path = PermissionEvents.String(input, "file_path") ?? PermissionEvents.String(input, "notebook_path");
        if (path is null)
            return null;

        if (result is { ValueKind: JsonValueKind.Object } reported)
        {
            if (reported.TryGetProperty("structuredPatch", out var patch) && patch.ValueKind == JsonValueKind.Array && patch.GetArrayLength() > 0)
                return FromPatch(path, patch);
            if (PermissionEvents.String(reported, "type") == "create" && PermissionEvents.String(reported, "content") is { } created)
                return Added(path, created, created: true);
        }

        switch (tool)
        {
            case "Edit":
                return Header(path) + Replaced(PermissionEvents.String(input, "old_string") ?? string.Empty, PermissionEvents.String(input, "new_string") ?? string.Empty);
            case "MultiEdit" when input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array:
                var text = new StringBuilder(Header(path));
                foreach (var edit in edits.EnumerateArray())
                    text.Append(Replaced(PermissionEvents.String(edit, "old_string") ?? string.Empty, PermissionEvents.String(edit, "new_string") ?? string.Empty));
                return text.ToString();
            case "Write" when PermissionEvents.String(input, "content") is { } content:
                return Added(path, content, created: false);
            case "NotebookEdit" when PermissionEvents.String(input, "edit_mode") != "delete" && PermissionEvents.String(input, "new_source") is { } source:
                return Added(path, source, created: false);
            default:
                return null;
        }
    }

    /// <summary>
    /// The facts Fleet keeps with a call for showing it: a file tool's diff (<c>diff</c>), and the answers to a question
    /// (<c>answers</c>, one list of labels per question). Null when there are none.
    /// </summary>
    internal static JsonElement? Metadata(string? diff, IReadOnlyList<IReadOnlyList<string>>? answers = null)
    {
        if (diff is null && answers is null)
            return null;

        return Write(json =>
        {
            json.WriteStartObject();
            if (diff is not null)
                json.WriteString("diff", diff);
            if (answers is not null)
            {
                json.WriteStartArray("answers");
                foreach (var answer in answers)
                {
                    json.WriteStartArray();
                    foreach (var label in answer)
                        json.WriteStringValue(label);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        });
    }

    /// <summary>A subagent call's metadata: the Fleet session its steps go to.</summary>
    internal static JsonElement ChildSession(string fleetSessionId) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("sessionId", fleetSessionId);
        json.WriteEndObject();
    });

    private static string Header(string path) => $"--- {path}\n+++ {path}\n";

    private static string Replaced(string oldText, string newText)
    {
        var removed = Lines(oldText);
        var added = Lines(newText);
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"@@ -1,{removed.Length} +1,{added.Length} @@\n");
        foreach (var line in removed)
            text.Append('-').Append(line).Append('\n');
        foreach (var line in added)
            text.Append('+').Append(line).Append('\n');
        return text.ToString();
    }

    private static string Added(string path, string content, bool created)
    {
        var lines = Lines(content);
        var text = new StringBuilder(created ? $"--- /dev/null\n+++ {path}\n" : Header(path));
        text.Append(CultureInfo.InvariantCulture, $"@@ -0,0 +1,{lines.Length} @@\n");
        foreach (var line in lines)
            text.Append('+').Append(line).Append('\n');
        return text.ToString();
    }

    /// <summary>Claude Code's <c>structuredPatch</c>: hunks of <c>oldStart</c>, <c>oldLines</c>, <c>newStart</c>, <c>newLines</c> and diff lines.</summary>
    private static string FromPatch(string path, JsonElement hunks)
    {
        var text = new StringBuilder(Header(path));
        foreach (var hunk in hunks.EnumerateArray())
        {
            if (hunk.ValueKind != JsonValueKind.Object)
                continue;
            text.Append(CultureInfo.InvariantCulture,
                $"@@ -{Number(hunk, "oldStart")},{Number(hunk, "oldLines")} +{Number(hunk, "newStart")},{Number(hunk, "newLines")} @@\n");
            if (hunk.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    if (line.ValueKind == JsonValueKind.String)
                        text.Append(line.GetString()).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    private static long Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>A text's lines, without the empty one after a final newline.</summary>
    private static string[] Lines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static void Rename(
        Utf8JsonWriter json, JsonElement input, Dictionary<string, string> renames, string? list, Dictionary<string, string>? itemRenames)
    {
        json.WriteStartObject();
        foreach (var property in input.EnumerateObject())
        {
            json.WritePropertyName(renames.GetValueOrDefault(property.Name, property.Name));
            if (property.Name == list && itemRenames is not null && property.Value.ValueKind == JsonValueKind.Array)
            {
                json.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        Rename(json, item, itemRenames, null, null);
                    else
                        item.WriteTo(json);
                }

                json.WriteEndArray();
            }
            else
            {
                property.Value.WriteTo(json);
            }
        }

        json.WriteEndObject();
    }

    private static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
            write(json);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
