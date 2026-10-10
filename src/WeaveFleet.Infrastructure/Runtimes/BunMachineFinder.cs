using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Finds the Buns on the machine Fleet can offer the user, in every build: <c>bun</c> on <c>PATH</c>, then
/// <c>~/.bun/bin</c>, <c>/opt/homebrew/bin</c> and <c>/usr/local/bin</c>; on Windows <c>bun.exe</c> on <c>PATH</c>
/// and in <c>%USERPROFILE%\.bun\bin</c>. Each file once, however many links lead to it.
/// </summary>
internal sealed class BunMachineFinder
{
    /// <summary>Test seam: the user's home folder.</summary>
    public required string Home { get; init; }

    /// <summary>Test seam: the <c>PATH</c> value to search.</summary>
    public string? PathEnv { get; init; } = Environment.GetEnvironmentVariable("PATH");

    /// <summary>Test seam: the fixed folders searched after <c>~/.bun/bin</c>, except on Windows.</summary>
    internal IReadOnlyList<string> SystemDirectories { get; init; } = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>Test seam: whether to look the way Windows does.</summary>
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Test seam: learns a Bun's version.</summary>
    public Func<string, CancellationToken, Task<BunProbeResult>> Probe { get; init; } =
        (path, ct) => BunVersionProbe.RunAsync(path, BunVersionProbe.DefaultTimeout, ct);

    /// <summary>Every Bun found, in search order, each with its version and status.</summary>
    public async Task<IReadOnlyList<BunCandidate>> FindAsync(CancellationToken ct)
    {
        // Each file once: the first place it's found wins, however many links lead to it.
        var comparer = CaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<(string Path, string Resolved)>();
        foreach (var directory in SearchDirectories())
        {
            var path = FullPathOrNull(Path.Combine(directory, ExecutableName));
            if (path is null || !IsFile(path))
                continue;

            var resolved = Canonical(path);
            if (seen.Add(resolved))
                found.Add((path, resolved));
        }

        // Probe them all at once; Task.WhenAll keeps the order they were found in.
        return await Task.WhenAll(found.Select(f => ClassifyAsync(f.Path, f.Resolved, ct))).ConfigureAwait(false);
    }

    /// <summary>Checks one path the user typed. A relative path, or one that doesn't exist, is not working.</summary>
    public async Task<BunCandidate> CheckAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return NotWorking(path, path, IsWindows
                ? @"Use the full path to bun, starting with a drive letter, like C:\Users\you\.bun\bin\bun.exe."
                : "Use the full path to bun, starting from /, like /home/you/.bun/bin/bun.");
        }

        var full = Path.GetFullPath(path);
        if (!IsFile(full))
            return NotWorking(full, full, $"There's no file at {full}.");

        return await ClassifyAsync(full, Canonical(full), ct).ConfigureAwait(false);
    }

    /// <summary>The file name to look for: <c>.exe</c> only on Windows, never a <c>.cmd</c> or <c>.bat</c> shim.</summary>
    private string ExecutableName => IsWindows ? "bun.exe" : "bun";

    private static bool CaseInsensitiveFileSystem => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The folders to look in, in order: <c>PATH</c>, then Bun's own folder, then the system folders.</summary>
    private IEnumerable<string> SearchDirectories()
    {
        var separator = IsWindows ? ';' : ':';
        foreach (var entry in (PathEnv ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry (such as ".") would search wherever Fleet happens to be running.
            var directory = IsWindows ? entry.Trim().Trim('"') : entry;
            if (directory.Length > 0 && Path.IsPathRooted(directory))
                yield return directory;
        }

        yield return Path.Combine(Home, ".bun", "bin");

        if (!IsWindows)
        {
            foreach (var directory in SystemDirectories)
                yield return directory;
        }
    }

    private async Task<BunCandidate> ClassifyAsync(string path, string resolved, CancellationToken ct)
    {
        var probe = await Probe(path, ct).ConfigureAwait(false);
        if (probe.Version is not { } version)
            return NotWorking(path, resolved, probe.Error ?? "Bun didn't say what version it is.");

        var oldest = BunVersion.Parse(BunRelease.MinimumVersion);
        return version.IsOlderThan(oldest)
            ? new BunCandidate(
                path, resolved, version.ToString(), BunCandidateStatuses.TooOld,
                $"Bun {version} is older than {oldest}, the oldest Bun mods run on. Run bun upgrade to update it.")
            : new BunCandidate(path, resolved, version.ToString(), BunCandidateStatuses.Usable, null);
    }

    private static BunCandidate NotWorking(string path, string resolved, string message) =>
        new(path, resolved, null, BunCandidateStatuses.NotWorking, message);

    /// <summary>A file that exists. A link counts only when what it leads to is a file: not a folder, not nothing.</summary>
    private static bool IsFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var link = new FileInfo(path).LinkTarget;
            if (link is null)
                return true;

            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && File.Exists(target.FullName) && !Directory.Exists(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path with every link followed, in the file and in each folder above it, so two paths to one file compare equal.
    /// A path that can't be resolved (a loop, a vanished link) is returned as it is.
    /// </summary>
    private static string Canonical(string path, int depth = 0)
    {
        const int MaxDepth = 16;
        var full = Path.GetFullPath(path);
        if (depth > MaxDepth)
            return full;

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is null)
                    continue;

                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Canonical(target.FullName, depth + 1);
            }
            catch (IOException)
            {
                // Can't follow it; keep the path as it is.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return current;
    }
}
