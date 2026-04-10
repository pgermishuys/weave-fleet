using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Scans workspace roots for git repositories and provides repository metadata.
/// Results are cached in-memory until explicitly invalidated.
/// </summary>
public sealed partial class RepositoryService(
    IServiceScopeFactory scopeFactory,
    ILogger<RepositoryService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, RepositoryInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _scanned;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public void Dispose() => _scanLock.Dispose();

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns all scanned repositories (triggers scan on first call).</summary>
    public async Task<IReadOnlyList<RepositoryInfo>> ScanRepositoriesAsync(CancellationToken ct = default)
    {
        if (!_scanned)
            await RefreshScanAsync(ct).ConfigureAwait(false);

        return [.. _cache.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Returns metadata for a single repository path.</summary>
    public async Task<RepositoryInfo?> GetRepositoryInfoAsync(string path, CancellationToken ct = default)
    {
        var normalised = WorkspaceRootService.CanonicalizePath(path);
        if (_cache.TryGetValue(normalised, out var cached))
            return cached;

        if (!IsGitRepo(normalised))
            return null;

        var info = await BuildRepositoryInfoAsync(normalised, ct).ConfigureAwait(false);
        _cache[normalised] = info;
        return info;
    }

    /// <summary>Returns enriched detail for a single repository (includes branch list, remotes, etc.).</summary>
    public async Task<RepositoryDetail?> GetRepositoryDetailAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = WorkspaceRootService.CanonicalizePath(path);
        var info = await GetRepositoryInfoAsync(normalizedPath, ct).ConfigureAwait(false);
        if (info is null)
            return null;

        var branches = await RunGitAsync(normalizedPath, ["branch", "--list", "--sort=-committerdate"], ct).ConfigureAwait(false);
        var remotes = await RunGitAsync(normalizedPath, ["remote", "-v"], ct).ConfigureAwait(false);
        var log = await RunGitAsync(normalizedPath, ["log", "--oneline", "-10"], ct).ConfigureAwait(false);

        return new RepositoryDetail(
            Info: info,
            Branches: ParseLines(branches),
            Remotes: ParseLines(remotes),
            RecentCommits: ParseLines(log));
    }

    public async Task<Result<string>> ResolveRepositoryPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Repository.Path", "Repository path is required.");

        IReadOnlyList<string> roots;
        using (var scope = scopeFactory.CreateScope())
        {
            var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
            roots = await workspaceRootService.GetAllowedRootsAsync().ConfigureAwait(false);
        }

        string normalizedPath;
        try
        {
            normalizedPath = WorkspaceRootService.CanonicalizePath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FleetError.ValidationError("Repository.Path", "Unable to resolve repository path.");
        }

        if (!WorkspaceRootService.IsPathWithinRoots(normalizedPath, roots))
            return FleetError.ValidationError("Repository.Path", "Repository path is outside allowed workspace roots.");

        if (!IsGitRepo(normalizedPath))
            return FleetError.ValidationError("Repository.Path", "Path is not a git repository.");

        return normalizedPath;
    }

    /// <summary>Clears the cache and re-scans all workspace roots.</summary>
    public async Task RefreshScanAsync(CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache.Clear();
            IReadOnlyList<string> roots;
            using (var scope = scopeFactory.CreateScope())
            {
                var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
                roots = await workspaceRootService.GetAllowedRootsAsync().ConfigureAwait(false);
            }

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                await ScanDirectoryAsync(root, root, maxDepth: 3, ct).ConfigureAwait(false);
            }

            _scanned = true;
            LogScanComplete(_cache.Count);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task ScanDirectoryAsync(string directory, string allowedRoot, int maxDepth, CancellationToken ct)
    {
        if (maxDepth <= 0 || ct.IsCancellationRequested)
            return;

        var canonicalDirectory = WorkspaceRootService.CanonicalizePath(directory);
        if (!WorkspaceRootService.IsPathWithinRoots(canonicalDirectory, [allowedRoot]))
            return;

        if (IsGitRepo(canonicalDirectory))
        {
            var info = await BuildRepositoryInfoAsync(canonicalDirectory, ct).ConfigureAwait(false);
            _cache[canonicalDirectory] = info;
            return; // don't recurse into git repos
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(canonicalDirectory))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;

                await ScanDirectoryAsync(sub, allowedRoot, maxDepth - 1, ct).ConfigureAwait(false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // skip inaccessible directories
        }
    }

    private static bool IsGitRepo(string path) =>
        Directory.Exists(Path.Combine(path, ".git"));

    private async Task<RepositoryInfo> BuildRepositoryInfoAsync(string path, CancellationToken ct)
    {
        var branchTask = RunGitAsync(path, ["branch", "--show-current"], ct);
        var remoteTask = RunGitAsync(path, ["remote", "get-url", "origin"], ct);
        var lastCommitTask = RunGitAsync(path, ["log", "-1", "--format=%s"], ct);

        await Task.WhenAll(branchTask, remoteTask, lastCommitTask).ConfigureAwait(false);

        return new RepositoryInfo(
            Path: path,
            Name: System.IO.Path.GetFileName(path),
            CurrentBranch: (await branchTask).Trim(),
            RemoteUrl: (await remoteTask).Trim(),
            LastCommitMessage: (await lastCommitTask).Trim());
    }

    private async Task<string> RunGitAsync(string workDir, string[] args, CancellationToken ct)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex)
        {
            var gitArgs = string.Join(' ', args);
            LogGitFailed(ex, gitArgs, workDir);
            return string.Empty;
        }
    }

    private static string[] ParseLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository scan complete: {Count} repositories found")]
    private partial void LogScanComplete(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "git {Args} failed in {Directory}")]
    private partial void LogGitFailed(Exception ex, string args, string directory);
}

/// <summary>Lightweight repository metadata.</summary>
public sealed record RepositoryInfo(
    string Path,
    string Name,
    string CurrentBranch,
    string RemoteUrl,
    string LastCommitMessage);

/// <summary>Enriched repository detail.</summary>
public sealed record RepositoryDetail(
    RepositoryInfo Info,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> Remotes,
    IReadOnlyList<string> RecentCommits);
