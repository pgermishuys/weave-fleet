namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Unpacks a zip archive Fleet downloaded without letting it write outside the folder it's unpacked into: no entry
/// whose path leaves the folder (<c>../</c>, an absolute path, a drive letter), no symbolic links, no entry twice, and
/// no more than a set number of bytes in all. Unix permission bits in the archive are kept (setuid, setgid and sticky
/// are dropped), so executables stay executable.
/// </summary>
internal static class SafeZipExtractor
{
    /// <summary>The most an archive may unpack to, unless the caller says otherwise: 512 MiB.</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Unpacks <paramref name="zipPath"/> into <paramref name="destination"/>, which must not exist yet or be empty.
    /// Every entry is checked before anything is written.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive has an entry it refuses, or unpacks to more than
    /// <paramref name="maxBytes"/>. The message names the entry. Files already written stay; the caller deletes
    /// <paramref name="destination"/>.</exception>
    public static void Extract(string zipPath, string destination, long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
        => throw new NotImplementedException();
}
