namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// File operations for skills and tools Fleet copies into harness folders.
/// </summary>
internal static class InstalledFiles
{
    /// <summary>Written by older Fleet versions into every skill folder it managed.</summary>
    public const string LegacyManagedMarker = ".fleet-managed";

    // Never copied into a harness folder: a nested .git would turn a skill inside a repository
    // into an embedded repository, and the marker is Fleet bookkeeping.
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        LegacyManagedMarker
    };

    /// <summary>True for a file, a folder, or a link, including a link whose target is gone.</summary>
    public static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || IsLink(path);

    public static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Where a link points, as a full path; null when <paramref name="path"/> isn't a link.</summary>
    public static string? LinkTarget(string path)
    {
        var target = new FileInfo(path).LinkTarget;
        if (target is null)
            return null;

        return Path.GetFullPath(target, Path.GetDirectoryName(path)!);
    }

    /// <summary>Deletes a file, a folder and its contents, or a link (never what the link points at).</summary>
    public static void Delete(string path)
    {
        if (IsLink(path))
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(path))
                Directory.Delete(path);
            else
                File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>Copies a folder, leaving out <c>.git</c> and Fleet's legacy marker.</summary>
    public static void CopyDirectory(string sourceDir, string targetDir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SkippedNames.Contains(Path.GetFileName(file)))
                continue;
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            if (SkippedNames.Contains(Path.GetFileName(subDir)))
                continue;
            CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)), cancellationToken);
        }
    }

    /// <summary>True when both folders hold the same files with the same bytes, ignoring what isn't copied.</summary>
    public static bool DirectoriesMatch(string left, string right)
    {
        if (!Directory.Exists(left) || !Directory.Exists(right))
            return false;

        var leftFiles = RelativeFiles(left);
        var rightFiles = RelativeFiles(right);
        if (!leftFiles.SetEquals(rightFiles))
            return false;

        return leftFiles.All(relative => FilesMatch(Path.Combine(left, relative), Path.Combine(right, relative)));
    }

    public static bool FilesMatch(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
            return false;

        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    public static bool SamePath(string left, string right) =>
        Normalize(left).Equals(Normalize(right), PathComparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or inside it.</summary>
    public static bool IsUnder(string path, string directory)
    {
        var fullPath = Normalize(path);
        var fullDirectory = Normalize(directory);

        return fullPath.Equals(fullDirectory, PathComparison) ||
               fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, PathComparison);
    }

    // Windows and macOS file systems are case-insensitive by default.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static HashSet<string> RelativeFiles(string root)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        Collect(root);
        return files;

        void Collect(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!SkippedNames.Contains(Path.GetFileName(file)))
                    files.Add(Path.GetRelativePath(root, file));
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedNames.Contains(Path.GetFileName(subDir)))
                    Collect(subDir);
            }
        }
    }
}
