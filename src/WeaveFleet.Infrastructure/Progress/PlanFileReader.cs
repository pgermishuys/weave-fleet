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
        if (Locate(sessionDirectory, path) is not { } located)
            return new PlanFileRead(PlanFileStatus.Refused);

        var (file, relative) = located;
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

    /// <summary>
    /// Takes what a tool call says it left in a file instead of reading the file, under the same rules: inside the
    /// session's folder, markdown, and no bigger than <see cref="MaxBytes"/>.
    /// </summary>
    public static PlanFileRead Accept(string sessionDirectory, string path, string content)
    {
        if (Locate(sessionDirectory, path) is not { } located || System.Text.Encoding.UTF8.GetByteCount(content) > MaxBytes)
            return new PlanFileRead(PlanFileStatus.Refused);

        return new PlanFileRead(PlanFileStatus.Read, located.RelativePath, content);
    }

    /// <summary>
    /// The real path of <paramref name="path"/> and where it is relative to the session's folder, with <c>/</c>
    /// separators; <see langword="null"/> when it's not a markdown file inside the folder.
    /// </summary>
    internal static (string File, string RelativePath)? Locate(string sessionDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(path) || !IsMarkdown(path))
            return null;

        string root;
        string file;
        try
        {
            root = RealPath(Path.GetFullPath(sessionDirectory));
            file = RealPath(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(sessionDirectory, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!file.StartsWith(rootWithSeparator, PathComparison) || !IsMarkdown(file))
            return null;

        return (file, Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
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
