using System.Collections.Concurrent;
using System.Diagnostics;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Scans a workspace directory for key project files (solutions, project files, build files).
/// Uses <c>git ls-files</c> to respect .gitignore. Results are cached with a 5-minute TTL.
/// </summary>
public sealed class KeyFileScanner
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly KeyFileConfig _config;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public KeyFileScanner(KeyFileConfig config)
    {
        _config = config;
    }

    public async Task<KeyFileResult> ScanAsync(string directory, CancellationToken ct = default)
    {
        var normalised = Path.GetFullPath(directory);

        if (_cache.TryGetValue(normalised, out var entry) && !entry.IsExpired)
            return entry.Result;

        var result = await ScanCoreAsync(normalised, ct);
        _cache[normalised] = new CacheEntry(result, DateTime.UtcNow);
        return result;
    }

    private async Task<KeyFileResult> ScanCoreAsync(string directory, CancellationToken ct)
    {
        var files = await ListFilesAsync(directory, ct);
        return BuildResult(files);
    }

    internal KeyFileResult BuildResult(IReadOnlyList<string> files)
    {
        // Determine which groups have matches
        var groupMatches = new Dictionary<string, List<string>>();

        foreach (var group in _config.Groups)
        {
            var matches = files.Where(f => MatchesGroup(f, group)).ToList();
            if (matches.Count > 0)
                groupMatches[group.Id] = matches;
        }

        // Apply trumping: if a group is active and trumps others, remove trumped groups
        var suppressedGroupIds = new HashSet<string>();
        foreach (var group in _config.Groups)
        {
            if (groupMatches.ContainsKey(group.Id) && group.Trumps is { Length: > 0 })
            {
                foreach (var trumped in group.Trumps)
                    suppressedGroupIds.Add(trumped);
            }
        }

        // Build per-tool file lists from non-suppressed groups
        var filesByTool = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupId, matches) in groupMatches)
        {
            if (suppressedGroupIds.Contains(groupId))
                continue;

            var group = _config.Groups.First(g => g.Id == groupId);
            foreach (var toolId in group.CompatibleTools)
            {
                if (!filesByTool.TryGetValue(toolId, out var list))
                {
                    list = [];
                    filesByTool[toolId] = list;
                }
                list.AddRange(matches);
            }
        }

        // Sort each tool's files: fewest path segments first, then alphabetical
        var sorted = filesByTool.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value
                .Distinct()
                .OrderBy(CountSegments)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new KeyFileResult(sorted);
    }

    private static bool MatchesGroup(string relativePath, KeyFileGroup group)
    {
        var fileName = Path.GetFileName(relativePath);

        if (group.Extensions is { Length: > 0 })
        {
            var ext = Path.GetExtension(relativePath);
            if (group.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        if (group.FileNames is { Length: > 0 })
        {
            if (group.FileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int CountSegments(string relativePath)
    {
        var normalised = relativePath.Replace('\\', '/');
        return normalised.Count(c => c == '/');
    }

    private static async Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                Arguments = "ls-files",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return FallbackScan(directory);

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                return FallbackScan(directory);

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch
        {
            return FallbackScan(directory);
        }
    }

    /// <summary>
    /// Fallback for non-git directories: enumerate files recursively,
    /// skipping common noise directories.
    /// </summary>
    private static List<string> FallbackScan(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => !IsInNoisyDirectory(f))
                .Select(f => Path.GetRelativePath(directory, f))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static readonly string[] NoisyDirectories = ["bin", "obj", "node_modules", ".git", "dist", "out", "build"];

    private static bool IsInNoisyDirectory(string fullPath)
    {
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => NoisyDirectories.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record CacheEntry(KeyFileResult Result, DateTime CreatedAt)
    {
        public bool IsExpired => DateTime.UtcNow - CreatedAt >= CacheTtl;
    }
}

public sealed record KeyFileResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> FilesByToolId);
