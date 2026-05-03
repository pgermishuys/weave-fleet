using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Applies patches to files using OpenCode's marker-based patch format.
/// Supports Add File, Update File, Delete File, and Move to operations.
/// Controlled by the "edit" permission (same as edit/write tools).
/// </summary>
internal sealed class ApplyPatchTool : INuCodeTool
{
    public string Name => "apply_patch";
    public string Description => "Apply patches to files. Supports Add File, Update File, Delete File, and Move to operations using OpenCode's marker-based patch format.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Apply patches to files using OpenCode's marker-based patch format.")]
    internal static async Task<string> ExecuteAsync(
        [Description(
            "The patch text to apply. Uses marker lines to describe operations:\n"
          + "  *** Add File: path/to/new-file.ts\n"
          + "  *** Update File: path/to/existing.ts\n"
          + "  *** Delete File: path/to/obsolete.ts\n"
          + "  *** Move to: path/to/renamed.ts\n"
          + "For Update File, include @@ unified diff hunks after the marker."
        )]
        string patchText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return "Error: patchText is required.";
        }

        var operations = ParsePatch(patchText);
        if (operations.Count == 0)
        {
            return "Error: No valid patch operations found in patchText.";
        }

        var results = new StringBuilder();
        var hasErrors = false;

        foreach (var op in operations)
        {
            var result = await ApplyOperationAsync(op, cancellationToken);
            results.AppendLine(result.Message);
            if (!result.Success)
            {
                hasErrors = true;
            }
        }

        var summary = results.ToString().TrimEnd();
        return hasErrors
            ? $"Patch applied with errors:\n{summary}"
            : $"Patch applied successfully.\n{summary}";
    }

    private static List<PatchOperation> ParsePatch(string patchText)
    {
        var operations = new List<PatchOperation>();
        var lines = patchText.Split('\n');
        PatchOperation? current = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("*** Add File:", StringComparison.Ordinal))
            {
                if (current is not null) operations.Add(current);
                current = new PatchOperation(PatchOperationType.Add, line["*** Add File:".Length..].Trim());
            }
            else if (line.StartsWith("*** Update File:", StringComparison.Ordinal))
            {
                if (current is not null) operations.Add(current);
                current = new PatchOperation(PatchOperationType.Update, line["*** Update File:".Length..].Trim());
            }
            else if (line.StartsWith("*** Delete File:", StringComparison.Ordinal))
            {
                if (current is not null) operations.Add(current);
                current = new PatchOperation(PatchOperationType.Delete, line["*** Delete File:".Length..].Trim());
            }
            else if (line.StartsWith("*** Move to:", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    current = current with { MoveTo = line["*** Move to:".Length..].Trim() };
                }
            }
            else if (current is not null)
            {
                current.Lines.Add(line);
            }
        }

        if (current is not null)
        {
            operations.Add(current);
        }

        return operations;
    }

    private static async Task<OperationResult> ApplyOperationAsync(PatchOperation op, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(op.FilePath);

        return op.Type switch
        {
            PatchOperationType.Add => await AddFileAsync(fullPath, op, ct),
            PatchOperationType.Update => await UpdateFileAsync(fullPath, op, ct),
            PatchOperationType.Delete => DeleteFile(fullPath, op),
            _ => new OperationResult(false, $"Unknown operation for '{op.FilePath}'."),
        };
    }

    private static async Task<OperationResult> AddFileAsync(string fullPath, PatchOperation op, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Collect content lines (skip any leading/trailing diff markers if present)
            var contentLines = op.Lines
                .Where(l => !l.StartsWith("@@", StringComparison.Ordinal))
                .Select(l => l.Length > 0 && l[0] == '+' ? l[1..] : l)
                .ToList();

            await File.WriteAllTextAsync(fullPath, string.Join("\n", contentLines), ct);
            return new OperationResult(true, $"Added: {op.FilePath}");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Error adding '{op.FilePath}': {ex.Message}");
        }
    }

    private static async Task<OperationResult> UpdateFileAsync(string fullPath, PatchOperation op, CancellationToken ct)
    {
        if (!File.Exists(fullPath))
        {
            return new OperationResult(false, $"Error: File not found for update: '{op.FilePath}'");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (IOException ex)
        {
            return new OperationResult(false, $"Error reading '{op.FilePath}': {ex.Message}");
        }

        // Parse unified diff hunks
        var hunkResult = ApplyHunks(content, op.Lines);
        if (!hunkResult.Success)
        {
            return new OperationResult(false, $"Error updating '{op.FilePath}': {hunkResult.Error}");
        }

        try
        {
            await File.WriteAllTextAsync(fullPath, hunkResult.Content, ct);
        }
        catch (IOException ex)
        {
            return new OperationResult(false, $"Error writing '{op.FilePath}': {ex.Message}");
        }

        // Handle rename/move
        if (op.MoveTo is not null)
        {
            var moveFullPath = Path.GetFullPath(op.MoveTo);
            try
            {
                var moveDir = Path.GetDirectoryName(moveFullPath);
                if (!string.IsNullOrEmpty(moveDir)) Directory.CreateDirectory(moveDir);
                File.Move(fullPath, moveFullPath, overwrite: true);
                return new OperationResult(true, $"Updated and moved: {op.FilePath} -> {op.MoveTo}");
            }
            catch (Exception ex)
            {
                return new OperationResult(false, $"Updated but move failed for '{op.FilePath}': {ex.Message}");
            }
        }

        return new OperationResult(true, $"Updated: {op.FilePath}");
    }

    private static OperationResult DeleteFile(string fullPath, PatchOperation op)
    {
        if (!File.Exists(fullPath))
        {
            return new OperationResult(false, $"Error: File not found for deletion: '{op.FilePath}'");
        }

        try
        {
            File.Delete(fullPath);
            return new OperationResult(true, $"Deleted: {op.FilePath}");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Error deleting '{op.FilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Applies unified diff hunks to file content.
    /// Each hunk starts with @@ -startLine,count +startLine,count @@ and contains context/add/remove lines.
    /// </summary>
    private static HunkApplyResult ApplyHunks(string content, List<string> lines)
    {
        var contentLines = content.Split('\n').ToList();
        // Track how many lines have been inserted/removed so far (offset for subsequent hunks)
        var offset = 0;

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Parse @@ -oldStart,oldCount +newStart,newCount @@
            var hunkHeader = ParseHunkHeader(line);
            if (hunkHeader is null)
            {
                i++;
                continue;
            }

            // Collect hunk body lines
            i++;
            var hunkLines = new List<string>();
            while (i < lines.Count && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                hunkLines.Add(lines[i]);
                i++;
            }

            // Apply this hunk
            var applyResult = ApplySingleHunk(contentLines, hunkLines, hunkHeader.Value.OldStart - 1 + offset);
            if (!applyResult.Success)
            {
                return new HunkApplyResult(false, applyResult.Error, "");
            }

            offset += applyResult.LineDelta;
            contentLines = applyResult.Lines;
        }

        return new HunkApplyResult(true, null, string.Join("\n", contentLines));
    }

    private static HunkHeader? ParseHunkHeader(string line)
    {
        // @@ -oldStart[,oldCount] +newStart[,newCount] @@
        var start = line.IndexOf('-');
        var plus = line.IndexOf('+');
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);

        if (start < 0 || plus < 0) return null;

        if (!TryParseRange(line[(start + 1)..(plus - 1)].Trim(), out var oldStart, out var oldCount))
        {
            return null;
        }

        var newRangeEnd = end > plus ? end : line.Length;
        if (!TryParseRange(line[(plus + 1)..newRangeEnd].Trim().TrimEnd('@', ' '), out var newStart, out _))
        {
            return null;
        }

        return new HunkHeader(oldStart, oldCount, newStart);
    }

    private static bool TryParseRange(string s, out int start, out int count)
    {
        start = 0;
        count = 1;
        var parts = s.Split(',');
        if (!int.TryParse(parts[0], out start)) return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], out count)) return false;
        return true;
    }

    private static SingleHunkResult ApplySingleHunk(List<string> lines, List<string> hunkLines, int startIndex)
    {
        var result = new List<string>(lines);
        var pos = startIndex;

        if (pos < 0) pos = 0;
        if (pos > result.Count) pos = result.Count;

        // Verify context lines match
        var checkPos = pos;
        foreach (var hunkLine in hunkLines)
        {
            if (hunkLine.Length == 0 || hunkLine[0] == ' ')
            {
                var contextLine = hunkLine.Length > 0 ? hunkLine[1..] : "";
                if (checkPos >= result.Count || result[checkPos] != contextLine)
                {
                    // Context mismatch — do a fuzzy search within ±3 lines
                    var fuzzyOffset = FindContextMatch(result, hunkLines, pos);
                    if (fuzzyOffset.HasValue)
                    {
                        pos = fuzzyOffset.Value;
                    }
                    else
                    {
                        return new SingleHunkResult(false, $"Context mismatch at line {checkPos + 1}: expected '{contextLine}'", [], 0);
                    }
                    break;
                }
                checkPos++;
            }
            else if (hunkLine[0] == '-')
            {
                checkPos++;
            }
        }

        // Apply the hunk
        var lineDelta = 0;
        var applyPos = pos;
        var newLines = new List<string>(result);

        // Build output from hunk
        var outputLines = new List<string>();
        var removeLines = new List<string>();

        foreach (var hunkLine in hunkLines)
        {
            if (hunkLine.Length == 0 || hunkLine[0] == ' ')
            {
                // Context line
                outputLines.Add(hunkLine.Length > 0 ? hunkLine[1..] : "");
                removeLines.Add(hunkLine.Length > 0 ? hunkLine[1..] : "");
            }
            else if (hunkLine[0] == '+')
            {
                outputLines.Add(hunkLine[1..]);
            }
            else if (hunkLine[0] == '-')
            {
                removeLines.Add(hunkLine[1..]);
            }
        }

        // Replace: remove the old lines (context + removed), insert output
        var removeCount = removeLines.Count;
        if (applyPos + removeCount <= newLines.Count)
        {
            newLines.RemoveRange(applyPos, removeCount);
        }
        else if (applyPos < newLines.Count)
        {
            newLines.RemoveRange(applyPos, newLines.Count - applyPos);
        }

        newLines.InsertRange(applyPos, outputLines);
        lineDelta = outputLines.Count - removeCount;

        return new SingleHunkResult(true, null, newLines, lineDelta);
    }

    private static int? FindContextMatch(List<string> lines, List<string> hunkLines, int startPos)
    {
        var contextLines = hunkLines
            .Where(l => l.Length == 0 || l[0] == ' ')
            .Select(l => l.Length > 0 ? l[1..] : "")
            .ToList();

        if (contextLines.Count == 0) return startPos;

        for (var offset = -3; offset <= 3; offset++)
        {
            var tryPos = startPos + offset;
            if (tryPos < 0 || tryPos + contextLines.Count > lines.Count) continue;

            var match = true;
            var checkIdx = 0;
            for (var j = tryPos; j < tryPos + contextLines.Count && checkIdx < contextLines.Count; j++, checkIdx++)
            {
                if (lines[j] != contextLines[checkIdx])
                {
                    match = false;
                    break;
                }
            }

            if (match) return tryPos;
        }

        return null;
    }

    private enum PatchOperationType { Add, Update, Delete }

    private sealed record PatchOperation(PatchOperationType Type, string FilePath)
    {
        public List<string> Lines { get; } = [];
        public string? MoveTo { get; set; }
    }

    private readonly record struct HunkHeader(int OldStart, int OldCount, int NewStart);

    private sealed record OperationResult(bool Success, string Message);

    private sealed record HunkApplyResult(bool Success, string? Error, string Content);

    private sealed record SingleHunkResult(bool Success, string? Error, List<string> Lines, int LineDelta);
}
