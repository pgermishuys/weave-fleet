using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Fast content search tool. Searches file contents using regular expressions.
/// Shells out to ripgrep (rg) if available, falls back to .NET regex search.
/// </summary>
internal sealed class GrepTool : INuCodeTool
{
    private const int DefaultMaxResults = 200;

    public string Name => "grep";
    public string Description => "Fast content search tool that searches file contents using regular expressions.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Search file contents using regular expressions. Returns file paths and line numbers with matches.")]
    private static async Task<string> ExecuteAsync(
        [Description("The regex pattern to search for in file contents.")] string pattern,
        [Description("File pattern to include in the search (e.g., \"*.cs\", \"*.{ts,tsx}\").")] string? include = null,
        [Description("The directory to search in. Defaults to working directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Error: pattern is required.";
        }

        var searchDir = path is not null ? Path.GetFullPath(path) : Directory.GetCurrentDirectory();

        if (!Directory.Exists(searchDir))
        {
            return $"Error: Directory does not exist: {searchDir}";
        }

        // Try ripgrep first, fall back to .NET search
        var rgResult = await TryRipgrepAsync(pattern, include, searchDir, cancellationToken);
        if (rgResult is not null)
        {
            return rgResult;
        }

        return FallbackSearch(pattern, include, searchDir, cancellationToken);
    }

    private static async Task<string?> TryRipgrepAsync(
        string pattern,
        string? include,
        string searchDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new StringBuilder();
            args.Append("--line-number --no-heading --color never ");

            if (include is not null)
            {
                args.Append($"--glob \"{include}\" ");
            }

            args.Append($"-- \"{pattern}\"");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rg",
                Arguments = args.ToString(),
                WorkingDirectory = searchDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode > 1) // rg: 0=match, 1=no match, 2+=error
            {
                return null; // Fall back to .NET search
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return "No matches found.";
            }

            // Limit output lines
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > DefaultMaxResults)
            {
                return string.Join('\n', lines.Take(DefaultMaxResults))
                     + $"\n(Showing {DefaultMaxResults} of {lines.Length}+ matches)";
            }

            return output.TrimEnd();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // rg not found — fall back to .NET search
            return null;
        }
    }

    private static string FallbackSearch(
        string pattern,
        string? include,
        string searchDir,
        CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException ex)
        {
            return $"Error: Invalid regex pattern: {ex.Message}";
        }

        var searchPattern = include ?? "*.*";
        var results = new List<string>();

        try
        {
            var files = Directory.EnumerateFiles(searchDir, searchPattern, SearchOption.AllDirectories);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (results.Count >= DefaultMaxResults)
                {
                    break;
                }

                try
                {
                    var relativePath = Path.GetRelativePath(searchDir, file);
                    var lines = File.ReadLines(file);
                    var lineNum = 0;

                    foreach (var line in lines)
                    {
                        lineNum++;
                        if (regex.IsMatch(line))
                        {
                            results.Add($"{relativePath}:{lineNum}:{line.TrimEnd()}");
                            if (results.Count >= DefaultMaxResults)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip files we can't read
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Error searching directory: {ex.Message}";
        }

        if (results.Count == 0)
        {
            return "No matches found.";
        }

        return string.Join('\n', results);
    }
}
