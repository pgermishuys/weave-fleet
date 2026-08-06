using System.Diagnostics;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Filters file system entries using .gitignore rules via git check-ignore.
/// Always excludes .git directory regardless of git availability.
/// </summary>
public sealed class GitIgnoreService
{
    private const string GitDirectoryName = ".git";

    /// <summary>
    /// Filters a list of relative paths, excluding .git and any paths matched by .gitignore.
    /// </summary>
    /// <param name="directoryPath">The directory containing the .gitignore file (typically the repo root or session directory).</param>
    /// <param name="relativePaths">Paths relative to <paramref name="directoryPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paths that are NOT ignored (i.e., should be shown).</returns>
    public static async Task<IReadOnlyList<string>> FilterIgnoredPathsAsync(
        string directoryPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default)
    {
        if (relativePaths.Count == 0)
            return Array.Empty<string>();

        // Always exclude .git
        var candidatePaths = relativePaths
            .Where(p => !string.Equals(p, GitDirectoryName, StringComparison.Ordinal))
            .ToList();

        if (candidatePaths.Count == 0)
            return Array.Empty<string>();

        // Try git check-ignore; if git is unavailable, fall back to no filtering (but .git is already excluded)
        var ignoredPaths = await GetGitIgnoredPathsAsync(directoryPath, candidatePaths, ct).ConfigureAwait(false);
        if (ignoredPaths is null)
            return candidatePaths;

        var ignoredSet = new HashSet<string>(ignoredPaths, StringComparer.Ordinal);
        return candidatePaths.Where(p => !ignoredSet.Contains(p)).ToList();
    }

    /// <summary>
    /// Runs `git check-ignore --stdin` to determine which paths are ignored.
    /// Returns null if git is not available or the command fails.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> GetGitIgnoredPathsAsync(
        string directoryPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "check-ignore --stdin",
                WorkingDirectory = directoryPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Write all paths to stdin, ignoring broken pipe if git exits early
            try
            {
                await using (var stdin = process.StandardInput)
                {
                    foreach (var path in relativePaths)
                    {
                        await stdin.WriteLineAsync(path.AsMemory(), ct).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException)
            {
                // Git process exited before we finished writing (e.g., not a git repo).
                // Continue to read whatever output is available.
            }

            // Read ignored paths from stdout
            var ignoredPaths = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ignoredPaths.Add(line.Trim());
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // git check-ignore exits with 0 if any paths are ignored, 1 if none are ignored, >1 on error
            if (process.ExitCode > 1)
                return null;

            return ignoredPaths;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            // Git not found or command failed — fall back to no filtering
            return null;
        }
    }
}
