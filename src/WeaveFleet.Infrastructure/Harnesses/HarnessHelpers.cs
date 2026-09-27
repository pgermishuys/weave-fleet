namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>Shared utilities for harness implementations.</summary>
internal static class HarnessHelpers
{
    /// <summary>
    /// Validates that <paramref name="directory"/> is a safe, absolute path
    /// that exists on disk. Throws <see cref="ArgumentException"/> if the path
    /// is relative, contains traversal sequences, or does not point to an existing directory.
    /// </summary>
    internal static void ValidateWorkingDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException(
                $"Working directory must be an absolute path: '{directory}'",
                nameof(directory));
        }

        // Reject explicit ".." segments to block path traversal regardless of OS normalization
        var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Array.Exists(parts, p => p == ".."))
        {
            throw new ArgumentException(
                $"Working directory must not contain path traversal sequences: '{directory}'",
                nameof(directory));
        }

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException(
                $"Working directory does not exist: '{directory}'",
                nameof(directory));
        }
    }

    /// <summary>
    /// On Windows, <paramref name="directory"/> spelled as it is on disk: a capital drive letter and each folder's own
    /// casing (<c>c:\source\app</c> becomes <c>C:\Source\app</c>). Elsewhere, and for a path that isn't on a drive, it's
    /// returned as given.
    /// </summary>
    /// <remarks>
    /// Git reports a folder's on-disk spelling, so a harness that compares the folder it was given with git's top level
    /// can disagree with itself. OpenCode 2 does, exactly: given <c>c:\…</c> its AGENTS.md walk never reaches git's
    /// <c>C:\…</c>, and every turn in the folder fails with "Instruction initialization blocked by unavailable sources".
    /// </remarks>
    internal static string OnDiskSpelling(string directory)
        => OperatingSystem.IsWindows() ? OnDiskSpelling(directory, NameOnDisk) : directory;

    /// <param name="directory">The folder, as the user gave it.</param>
    /// <param name="nameOnDisk">
    /// The name a folder's child has on disk, given the parent and a spelling of the name that ignores case; <see
    /// langword="null"/> when there's no such child.
    /// </param>
    internal static string OnDiskSpelling(string directory, Func<string, string, string?> nameOnDisk)
    {
        if (directory.Length < 3 || !char.IsAsciiLetter(directory[0]) || directory[1] != ':' || !IsSeparator(directory[2]))
            return directory;

        var spelled = new System.Text.StringBuilder(directory.Length).Append(char.ToUpperInvariant(directory[0])).Append(':');
        var index = 2;
        var found = true;
        while (index < directory.Length)
        {
            var start = index;
            while (index < directory.Length && IsSeparator(directory[index]))
                index++;
            spelled.Append(directory, start, index - start);

            start = index;
            while (index < directory.Length && !IsSeparator(directory[index]))
                index++;
            var name = directory[start..index];
            if (name is "" or ".")
            {
                spelled.Append(name);
                continue;
            }

            // Once a folder isn't there, nothing under it is either: the rest stays as given.
            var onDisk = found ? nameOnDisk(spelled.ToString(), name) : null;
            found = onDisk is not null;
            spelled.Append(onDisk ?? name);
        }

        return spelled.ToString();
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>The name <paramref name="parent"/>'s child folder <paramref name="name"/> has on disk, ignoring case.</summary>
    internal static string? NameOnDisk(string parent, string name)
    {
        if (name.AsSpan().IndexOfAny('*', '?') >= 0)
            return null;

        try
        {
            return new DirectoryInfo(parent)
                .EnumerateDirectories(name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 })
                .FirstOrDefault()?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
