using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Applies multiple sequential edits to a single file. Each edit sees the file
/// after all previous edits have been applied. Thin wrapper around <see cref="EditTool"/>.
/// </summary>
internal sealed class MultiEditTool : INuCodeTool
{
    public string Name => "multiedit";

    public string Description => "Performs multiple sequential string replacements in a single file.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Performs multiple sequential string replacements in a single file. Each edit sees the result of previous edits.")]
    internal static async Task<string> ExecuteAsync(
        [Description("The absolute path to the file to modify")] string filePath,
        [Description("Array of edits, each with 'oldString', 'newString', and optional 'replaceAll' (bool)")] JsonElement edits,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is required.";
        }

        if (edits.ValueKind != JsonValueKind.Array)
        {
            return "Error: edits must be an array.";
        }

        var editList = new List<EditSpec>();

        foreach (var element in edits.EnumerateArray())
        {
            if (!element.TryGetProperty("oldString", out var oldProp) || oldProp.ValueKind != JsonValueKind.String)
            {
                return "Error: Each edit must have an 'oldString' property (string).";
            }

            if (!element.TryGetProperty("newString", out var newProp) || newProp.ValueKind != JsonValueKind.String)
            {
                return "Error: Each edit must have a 'newString' property (string).";
            }

            var replaceAll = element.TryGetProperty("replaceAll", out var raProp)
                && raProp.ValueKind == JsonValueKind.True;

            editList.Add(new EditSpec(oldProp.GetString()!, newProp.GetString()!, replaceAll));
        }

        if (editList.Count == 0)
        {
            return "Error: edits array must not be empty.";
        }

        var fullPath = Path.GetFullPath(filePath);

        if (Directory.Exists(fullPath))
        {
            return $"Error: Path is a directory, not a file: {fullPath}";
        }

        if (!File.Exists(fullPath))
        {
            return $"Error: File not found: {fullPath}";
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        }
        catch (IOException ex)
        {
            return $"Error reading file: {ex.Message}";
        }

        // Detect file line endings
        var useCrlf = content.Contains("\r\n");

        // Apply edits sequentially
        for (var i = 0; i < editList.Count; i++)
        {
            var edit = editList[i];
            var normalizedOld = NormalizeLineEndings(edit.OldString, useCrlf);
            var normalizedNew = NormalizeLineEndings(edit.NewString, useCrlf);

            if (string.IsNullOrEmpty(normalizedOld))
            {
                return $"Error in edit {i}: oldString must not be empty.";
            }

            if (normalizedOld == normalizedNew)
            {
                return $"Error in edit {i}: oldString and newString are identical.";
            }

            var occurrences = CountOccurrences(content, normalizedOld);

            if (occurrences == 0)
            {
                return $"Error in edit {i}: oldString not found in file.";
            }

            if (occurrences > 1 && !edit.ReplaceAll)
            {
                return $"Error in edit {i}: Found {occurrences} matches for oldString. Set replaceAll to true or provide more context.";
            }

            content = edit.ReplaceAll
                ? content.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
                : ReplaceFirst(content, normalizedOld, normalizedNew);
        }

        try
        {
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }

        return $"All {editList.Count} edits applied successfully.";
    }

    private static string NormalizeLineEndings(string text, bool useCrlf)
    {
        var normalized = text.Replace("\r\n", "\n");
        return useCrlf ? normalized.Replace("\n", "\r\n") : normalized;
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }
        return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
    }

    private sealed record EditSpec(string OldString, string NewString, bool ReplaceAll);
}
