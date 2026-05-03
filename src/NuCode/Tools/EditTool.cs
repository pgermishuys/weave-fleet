using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Performs exact string replacements in files. Supports single or all-occurrence replacement
/// with automatic line-ending normalization.
/// </summary>
internal sealed class EditTool : INuCodeTool
{
    public string Name => "edit";
    public string Description => "Performs exact string replacements in files.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Performs exact string replacements in files.")]
    private static async Task<string> ExecuteAsync(
        [Description("The absolute path to the file to modify")] string filePath,
        [Description("The text to replace")] string oldString,
        [Description("The text to replace it with (must be different from oldString)")] string newString,
        [Description("Replace all occurrences of oldString (default false)")] bool? replaceAll = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is required.";
        }

        if (string.IsNullOrEmpty(oldString))
        {
            return "Error: oldString is required.";
        }

        if (newString is null)
        {
            return "Error: newString is required.";
        }

        if (oldString == newString)
        {
            return "No changes to apply: oldString and newString are identical.";
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

        // Detect file line endings and normalize search/replace strings to match
        var useCrlf = content.Contains("\r\n");
        var normalizedOld = NormalizeLineEndings(oldString, useCrlf);
        var normalizedNew = NormalizeLineEndings(newString, useCrlf);

        var occurrences = CountOccurrences(content, normalizedOld);

        if (occurrences == 0)
        {
            return "Error: oldString not found in content";
        }

        var shouldReplaceAll = replaceAll ?? false;

        if (occurrences > 1 && !shouldReplaceAll)
        {
            return "Error: Found multiple matches for oldString. Provide more surrounding lines in oldString to identify the correct match.";
        }

        var modified = shouldReplaceAll
            ? content.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
            : ReplaceFirst(content, normalizedOld, normalizedNew);

        try
        {
            await File.WriteAllTextAsync(fullPath, modified, cancellationToken);
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }

        return "Edit applied successfully.";
    }

    private static string NormalizeLineEndings(string text, bool useCrlf)
    {
        // First normalize all line endings to \n, then convert to target if needed
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
}
