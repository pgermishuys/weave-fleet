using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace NuCode.Tools;

/// <summary>
/// Fast file pattern matching tool. Supports glob patterns like "**/*.cs".
/// Returns matching file paths sorted by modification time.
/// </summary>
internal sealed class GlobTool : INuCodeTool
{
    private const int DefaultMaxResults = 1000;

    public string Name => "glob";
    public string Description => "Fast file pattern matching tool that works with any codebase size.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Fast file pattern matching tool. Returns matching file paths sorted by modification time.")]
    private static Task<string> ExecuteAsync(
        [Description("The glob pattern to match files against (e.g., \"**/*.cs\", \"src/**/*.ts\").")] string pattern,
        [Description("The directory to search in. Defaults to working directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult("Error: pattern is required.");
        }

        var searchDir = path is not null ? Path.GetFullPath(path) : Directory.GetCurrentDirectory();

        if (!Directory.Exists(searchDir))
        {
            return Task.FromResult($"Error: Directory does not exist: {searchDir}");
        }

        var matcher = new Matcher();
        matcher.AddInclude(pattern);

        var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchDir));
        var result = matcher.Execute(directoryInfo);

        if (!result.HasMatches)
        {
            return Task.FromResult("No matches found.");
        }

        // Get file info with modification times, sorted by mtime descending
        var files = result.Files
            .Select(match =>
            {
                var fullPath = Path.Combine(searchDir, match.Path);
                try
                {
                    var info = new FileInfo(fullPath);
                    return new { Path = match.Path, ModTime = info.LastWriteTimeUtc, Exists = info.Exists };
                }
                catch
                {
                    return new { Path = match.Path, ModTime = DateTime.MinValue, Exists = false };
                }
            })
            .Where(f => f.Exists)
            .OrderByDescending(f => f.ModTime)
            .Take(DefaultMaxResults)
            .ToList();

        if (files.Count == 0)
        {
            return Task.FromResult("No matches found.");
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            sb.AppendLine(file.Path);
        }

        if (result.Files.Count() > DefaultMaxResults)
        {
            sb.AppendLine($"(Showing {DefaultMaxResults} of {result.Files.Count()} matches)");
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
