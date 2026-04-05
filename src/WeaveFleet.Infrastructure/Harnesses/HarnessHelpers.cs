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
}
