using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Walkthroughs;

/// <summary>
/// The outline an agent sends with <c>fleet_walkthrough_show</c>: the overview, the chapters and the files each one
/// covers. The hunks aren't in it: Fleet takes them from the change itself, so the agent spends no tokens on code it
/// would only copy. <see cref="Parse"/> says in words what's wrong, for the agent to fix and call again.
/// </summary>
public sealed record WalkthroughGuide(
    string Summary,
    IReadOnlyList<WalkthroughStep> Steps,
    JsonObject? Diagram,
    IReadOnlyList<WalkthroughChapter> Chapters,
    IReadOnlyList<WalkthroughAlsoChanged> AlsoChanged,
    string? From,
    string? To)
{
    public const int MaxChapters = 30;

    private const int MaxErrors = 12;

    /// <summary>The guide in <paramref name="guide"/>, or what's wrong with it.</summary>
    public static (WalkthroughGuide? Guide, IReadOnlyList<string> Errors) Parse(JsonElement guide)
    {
        var errors = new List<string>();
        if (guide.ValueKind != JsonValueKind.Object)
            return (null, ["\"guide\" must be an object: {\"summary\", \"chapters\": [...], ...}."]);

        var summary = Text(guide, "summary");
        if (summary is null)
            errors.Add("guide.summary is required: two or three sentences on what the change does and why.");

        var chapters = new List<WalkthroughChapter>();
        if (!guide.TryGetProperty("chapters", out var chapterList) || chapterList.ValueKind != JsonValueKind.Array || chapterList.GetArrayLength() == 0)
            errors.Add("guide.chapters is required: one or more chapters, each {\"title\", \"body\", \"files\"}.");
        else if (chapterList.GetArrayLength() > MaxChapters)
            errors.Add($"guide.chapters has {chapterList.GetArrayLength()} chapters; {MaxChapters} at most. Group the change into fewer.");
        else
        {
            var index = 0;
            foreach (var chapter in chapterList.EnumerateArray())
                chapters.Add(ParseChapter(chapter, $"guide.chapters[{index++}]", errors));
        }

        var steps = new List<WalkthroughStep>();
        if (guide.TryGetProperty("steps", out var stepList) && stepList.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var step in stepList.EnumerateArray())
            {
                var name = $"guide.steps[{index++}]";
                if (step.ValueKind == JsonValueKind.String && Clean(step.GetString()) is { } plain)
                    steps.Add(new WalkthroughStep(plain, null));
                else if (Text(step, "text") is { } text)
                    steps.Add(new WalkthroughStep(text, ChapterNumber(step, "chapter", name, chapters.Count, errors)));
                else
                    errors.Add($"{name} needs \"text\".");
            }
        }

        JsonObject? diagram = null;
        if (guide.TryGetProperty("diagram", out var diagramElement) && diagramElement.ValueKind != JsonValueKind.Null)
            diagram = ParseDiagram(diagramElement, chapters.Count, errors);

        var also = new List<WalkthroughAlsoChanged>();
        if (guide.TryGetProperty("alsoChanged", out var alsoList) && alsoList.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in alsoList.EnumerateArray())
            {
                var name = $"guide.alsoChanged[{index++}]";
                if (item.ValueKind == JsonValueKind.String && Clean(item.GetString()) is { } path)
                    also.Add(new WalkthroughAlsoChanged(path, null));
                else if (Text(item, "path") is { } itemPath)
                    also.Add(new WalkthroughAlsoChanged(itemPath, Text(item, "note")));
                // A pair, as a model may write it: ["package-lock.json", "lock file"].
                else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() is 1 or 2
                         && item[0].ValueKind == JsonValueKind.String && Clean(item[0].GetString()) is { } pairPath)
                    also.Add(new WalkthroughAlsoChanged(pairPath, item.GetArrayLength() == 2 && item[1].ValueKind == JsonValueKind.String ? Clean(item[1].GetString()) : null));
                else
                    errors.Add($"{name} must be a path, or {{\"path\", \"note\"}}.");
            }
        }

        var from = Text(guide, "from");
        var to = Text(guide, "to");
        if (to is not null && from is null)
            errors.Add("guide.to needs guide.from: the walkthrough shows from...to.");

        return errors.Count > 0 || summary is null
            ? (null, errors.Take(MaxErrors).ToList())
            : (new WalkthroughGuide(summary, steps, diagram, chapters, also, from, to), []);
    }

    private static WalkthroughChapter ParseChapter(JsonElement chapter, string name, List<string> errors)
    {
        if (chapter.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{name} must be {{\"title\", \"body\", \"files\"}}.");
            return new WalkthroughChapter(string.Empty, [], null, [], []);
        }

        var title = Text(chapter, "title");
        if (title is null)
            errors.Add($"{name}.title is required.");

        var body = Texts(chapter, "body");
        if (body.Count == 0)
            errors.Add($"{name}.body is required: what changed in plain words, and why it's here.");

        var files = new List<WalkthroughFile>();
        if (chapter.TryGetProperty("files", out var fileList) && fileList.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var file in fileList.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.String && Clean(file.GetString()) is { } path)
                    files.Add(new WalkthroughFile(path, Collapsed: false));
                else if (Text(file, "path") is { } objectPath)
                    files.Add(new WalkthroughFile(objectPath, file.TryGetProperty("collapsed", out var collapsed) && collapsed.ValueKind == JsonValueKind.True));
                else
                    errors.Add($"{name}.files[{index}] must be a path, or {{\"path\", \"collapsed\"}}.");
                index++;
            }
        }

        if (files.Count == 0)
            errors.Add($"{name}.files is required: the changed files this chapter is about.");

        return new WalkthroughChapter(title ?? string.Empty, body, Text(chapter, "cite"), files, Texts(chapter, "closer"));
    }

    /// <summary>The diagram as the page draws it, its chapter numbers checked: <c>{caption, before?, after}</c>.</summary>
    private static JsonObject? ParseDiagram(JsonElement diagram, int chapters, List<string> errors)
    {
        if (diagram.ValueKind != JsonValueKind.Object || !diagram.TryGetProperty("after", out var after))
        {
            errors.Add("guide.diagram must be {\"caption\", \"before\"?, \"after\": {\"rows\": [...]}}.");
            return null;
        }

        var result = new JsonObject { ["caption"] = Text(diagram, "caption") };
        if (diagram.TryGetProperty("before", out var before) && before.ValueKind == JsonValueKind.Object)
        {
            var side = ParseSide(before, "guide.diagram.before", chapters, errors);
            side["problem"] = Text(before, "problem");
            result["before"] = side;
        }

        result["after"] = ParseSide(after, "guide.diagram.after", chapters, errors);
        return result;
    }

    private static JsonObject ParseSide(JsonElement side, string name, int chapters, List<string> errors)
    {
        var rows = new JsonArray();
        if (side.ValueKind != JsonValueKind.Object || !side.TryGetProperty("rows", out var rowList) || rowList.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{name}.rows is required: [{{\"label\"?, \"branch\"?, \"nodes\": [...]}}].");
            return new JsonObject { ["rows"] = rows };
        }

        var r = 0;
        foreach (var row in rowList.EnumerateArray())
        {
            var rowName = $"{name}.rows[{r++}]";
            var nodes = new JsonArray();
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("nodes", out var nodeList) && nodeList.ValueKind == JsonValueKind.Array)
            {
                var n = 0;
                foreach (var node in nodeList.EnumerateArray())
                {
                    var nodeName = $"{rowName}.nodes[{n++}]";
                    if (Text(node, "text") is not { } text)
                    {
                        errors.Add($"{nodeName} needs \"text\".");
                        continue;
                    }

                    var kind = Text(node, "kind");
                    nodes.Add((JsonNode)new JsonObject
                    {
                        ["text"] = text,
                        ["code"] = node.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.True,
                        ["chapter"] = ChapterNumber(node, "chapter", nodeName, chapters, errors),
                        ["kind"] = kind is "new" or "gone" ? kind : null,
                        ["note"] = Text(node, "note"),
                    });
                }
            }

            if (nodes.Count == 0)
            {
                errors.Add($"{rowName}.nodes needs one or more {{\"text\"}}.");
                continue;
            }

            rows.Add((JsonNode)new JsonObject
            {
                ["label"] = Text(row, "label"),
                ["branch"] = row.TryGetProperty("branch", out var branch) && branch.ValueKind == JsonValueKind.True,
                ["nodes"] = nodes,
            });
        }

        return new JsonObject { ["rows"] = rows };
    }

    private static int? ChapterNumber(JsonElement element, string property, string name, int chapters, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 1 && number <= chapters)
            return number;

        errors.Add($"{name}.{property} must be a chapter number from 1 to {chapters.ToString(CultureInfo.InvariantCulture)}.");
        return null;
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString())
            : null;

    /// <summary>A string, or an array of them, as a list of non-empty strings.</summary>
    private static List<string> Texts(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return Clean(value.GetString()) is { } one ? [one] : [];
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        return [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => Clean(item.GetString())).OfType<string>()];
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

public sealed record WalkthroughStep(string Text, int? Chapter);

public sealed record WalkthroughChapter(
    string Title, IReadOnlyList<string> Body, string? Cite, IReadOnlyList<WalkthroughFile> Files, IReadOnlyList<string> Closer);

public sealed record WalkthroughFile(string Path, bool Collapsed);

/// <summary>A changed file, or a pattern of them (<c>*</c> within a folder, <c>**</c> across folders), left out of the chapters.</summary>
public sealed record WalkthroughAlsoChanged(string Path, string? Note);
