using System.Text.Json;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// Pi's file tools, <c>write</c> (<c>path</c>, <c>content</c>) and <c>edit</c> (<c>path</c>, <c>edits</c>: each an
/// <c>oldText</c> and its <c>newText</c>), as Fleet reads them: which file a finished call wrote and what it did to it.
/// Pi reports a call's arguments as the model sent them, before Pi tidies them, so <c>edits</c> may come as a JSON string
/// and an older model may send one <c>oldText</c>/<c>newText</c> beside them.
/// </summary>
internal static class PiFileTools
{
    internal const string Write = "write";
    internal const string Edit = "edit";

    /// <summary>
    /// The file a <c>write</c> or <c>edit</c> call wrote, against <paramref name="workingDirectory"/> when relative, as Pi
    /// resolves it (a leading <c>@</c> dropped, <c>~</c> the home folder). Null for any other tool, or a call with no path.
    /// </summary>
    internal static string? WrittenFile(string toolName, JsonElement? args, string? workingDirectory)
    {
        if (toolName is not (Write or Edit) || args is not { ValueKind: JsonValueKind.Object } input
            || PermissionEvents.String(input, "path") is not { Length: > 0 } path)
            return null;

        if (path.StartsWith('@'))
            path = path[1..];
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'));
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return workingDirectory is null ? path : Path.GetFullPath(Path.Combine(workingDirectory, path));
    }

    /// <summary>
    /// What a finished <c>write</c> or <c>edit</c> call did to <paramref name="path"/>: <c>write</c>'s whole file, or an
    /// <c>edit</c>'s replacements. Pi matches each of an edit's texts against the file as it was, and refuses texts that
    /// aren't there exactly once or that overlap, so making them one after another, each once, leaves the same file; when
    /// it wouldn't (Pi matched a text loosely, ignoring trailing spaces or quote styles), the text isn't in the file exactly
    /// once and Fleet reads the file instead. None when the call says nothing Fleet can apply.
    /// </summary>
    internal static IReadOnlyList<FileChange> Changes(string toolName, JsonElement? args, string path)
    {
        if (args is not { ValueKind: JsonValueKind.Object } input)
            return [];

        if (toolName == Write)
            return PermissionEvents.String(input, "content") is { } content ? [new FileChange { Path = path, Content = content }] : [];
        if (toolName != Edit)
            return [];

        var changes = new List<FileChange>();
        if (input.TryGetProperty("edits", out var edits))
        {
            if (edits.ValueKind == JsonValueKind.String && Parse(edits.GetString()) is { } parsed)
                edits = parsed;
            if (edits.ValueKind != JsonValueKind.Array)
                return [];
            foreach (var item in edits.EnumerateArray())
            {
                // One edit Fleet can't apply leaves the ones after it applied to the wrong text: say none, so Fleet reads the file.
                if (Replacement(item, path) is not { } change)
                    return [];
                changes.Add(change);
            }
        }

        // An older model's one replacement, which Pi makes after any in edits.
        if (PermissionEvents.String(input, "oldText") is not null || PermissionEvents.String(input, "newText") is not null)
        {
            if (Replacement(input, path) is not { } change)
                return [];
            changes.Add(change);
        }

        return changes;
    }

    private static FileChange? Replacement(JsonElement edit, string path)
        => edit.ValueKind == JsonValueKind.Object
           && PermissionEvents.String(edit, "oldText") is { Length: > 0 } oldText
           && PermissionEvents.String(edit, "newText") is { } newText
            ? new FileChange { Path = path, OldString = oldText, NewString = newText }
            : null;

    private static JsonElement? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
