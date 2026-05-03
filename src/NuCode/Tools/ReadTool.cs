using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Reads files and directories. Returns file contents with line numbers, directory listings,
/// binary file detection, and image file support.
/// </summary>
internal sealed class ReadTool : INuCodeTool
{
    private const int DefaultLimit = 2000;
    private const int MaxLineLength = 2000;

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
    };

    private static readonly HashSet<string> s_binaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".o", ".a",
        ".lib", ".zip", ".tar", ".gz", ".7z", ".rar",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".class", ".pyc", ".pdb", ".wasm",
    };

    public string Name => "read";
    public string Description => "Read a file or directory from the local filesystem.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Read a file or directory from the local filesystem.")]
    private static async Task<string> ExecuteAsync(
        [Description("The absolute path to the file or directory to read.")] string filePath,
        [Description("The line number to start reading from (1-indexed).")] int? offset = null,
        [Description("The maximum number of lines to read (defaults to 2000).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is required.";
        }

        if (offset.HasValue && offset.Value < 1)
        {
            return "Error: offset must be greater than or equal to 1.";
        }

        var fullPath = Path.GetFullPath(filePath);

        if (Directory.Exists(fullPath))
        {
            return ReadDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return $"Error: Path does not exist: {fullPath}";
        }

        var extension = Path.GetExtension(fullPath);

        if (s_binaryExtensions.Contains(extension))
        {
            return $"[binary file: {extension}]";
        }

        if (s_imageExtensions.Contains(extension))
        {
            return $"[image file: {Path.GetFileName(fullPath)}]";
        }

        return await ReadFileAsync(fullPath, offset ?? 1, limit ?? DefaultLimit, cancellationToken);
    }

    private static string ReadDirectory(string directoryPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<path>{directoryPath}</path>");
        sb.AppendLine("<type>directory</type>");
        sb.AppendLine("<entries>");

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(directoryPath)
                .Select(entry =>
                {
                    var name = Path.GetFileName(entry);
                    return Directory.Exists(entry) ? name + "/" : name;
                })
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                sb.AppendLine(entry);
            }
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine("[access denied]");
        }

        sb.AppendLine("</entries>");
        return sb.ToString();
    }

    private static async Task<string> ReadFileAsync(
        string filePath,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        string[] allLines;
        try
        {
            allLines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        }
        catch (IOException ex)
        {
            return $"Error reading file: {ex.Message}";
        }

        // If file appears to be binary (contains null bytes in first chunk), report it
        if (allLines.Length > 0 && IsBinaryContent(allLines[0]))
        {
            return $"[binary file: {Path.GetExtension(filePath)}]";
        }

        var startIndex = offset - 1; // Convert 1-indexed to 0-indexed
        if (startIndex >= allLines.Length)
        {
            return $"Error: offset {offset} is beyond end of file ({allLines.Length} lines).";
        }

        var endIndex = Math.Min(startIndex + limit, allLines.Length);

        var sb = new StringBuilder();
        sb.AppendLine($"<path>{filePath}</path>");
        sb.AppendLine("<type>file</type>");
        sb.AppendLine("<content>");

        for (var i = startIndex; i < endIndex; i++)
        {
            var lineNum = i + 1; // Back to 1-indexed for display
            var line = allLines[i];

            // Truncate long lines
            if (line.Length > MaxLineLength)
            {
                line = string.Concat(line.AsSpan(0, MaxLineLength), "...[truncated]");
            }

            sb.AppendLine($"{lineNum}: {line}");
        }

        sb.AppendLine("</content>");

        if (endIndex < allLines.Length)
        {
            sb.AppendLine($"(Showing lines {offset}-{endIndex} of {allLines.Length} total)");
        }

        return sb.ToString();
    }

    private static bool IsBinaryContent(string firstLine)
    {
        // Check for null bytes which indicate binary content
        return firstLine.Contains('\0');
    }
}
