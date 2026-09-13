namespace WeaveFleet.Infrastructure.Progress;

/// <summary>What reading a possible plan file found.</summary>
internal enum PlanFileStatus
{
    /// <summary>Not a file Fleet will read: outside the session's folder, not markdown, or too big.</summary>
    Refused,

    /// <summary>A markdown file inside the session's folder that isn't there (any more).</summary>
    Missing,

    Read,
}

/// <param name="RelativePath">Where the file is relative to the session's folder, with <c>/</c> separators.</param>
internal readonly record struct PlanFileRead(PlanFileStatus Status, string? RelativePath = null, string? Content = null);

/// <summary>
/// Reads markdown files a session wrote, for plan tracking. Only files inside the session's own folder, after
/// following symlinks, only <c>.md</c>, and only up to <see cref="MaxBytes"/>.
/// </summary>
internal static class PlanFileReader
{
    public const long MaxBytes = 256 * 1024;

    private const int MaxLinkDepth = 16;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    public static async Task<PlanFileRead> ReadAsync(string sessionDirectory, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(path) || !IsMarkdown(path))
            return new PlanFileRead(PlanFileStatus.Refused);

        string root;
        string file;
        try
        {
            root = RealPath(Path.GetFullPath(sessionDirectory));
            file = RealPath(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(sessionDirectory, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PlanFileRead(PlanFileStatus.Refused);
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!file.StartsWith(rootWithSeparator, PathComparison) || !IsMarkdown(file))
            return new PlanFileRead(PlanFileStatus.Refused);

        var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
        var info = new FileInfo(file);
        if (!info.Exists)
            return new PlanFileRead(PlanFileStatus.Missing, relative);
        if (info.Length > MaxBytes)
            return new PlanFileRead(PlanFileStatus.Refused);

        try
        {
            return new PlanFileRead(PlanFileStatus.Read, relative, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return new PlanFileRead(PlanFileStatus.Missing, relative);
        }
        catch (DirectoryNotFoundException)
        {
            return new PlanFileRead(PlanFileStatus.Missing, relative);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PlanFileRead(PlanFileStatus.Refused);
        }
    }

    /// <summary>The path with every symlink along it followed, as far as the path exists.</summary>
    internal static string RealPath(string fullPath, int depth = 0)
    {
        if (depth > MaxLinkDepth)
            throw new IOException("Too many symbolic links.");

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        var parts = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                var rest = parts.Length > i + 1 ? Path.Combine(parts[(i + 1)..]) : string.Empty;
                return RealPath(Path.GetFullPath(Path.Combine(target.FullName, rest)), depth + 1);
            }

            current = next;
        }

        return current;
    }
}
