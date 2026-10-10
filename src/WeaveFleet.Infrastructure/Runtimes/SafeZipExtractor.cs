using System.IO.Compression;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Unpacks a zip archive Fleet downloaded without letting it write outside the folder it's unpacked into: no entry
/// whose path leaves the folder (<c>../</c>, an absolute path, a drive letter), no symbolic links, no entry twice, and
/// no more than a set number of bytes in all, and no entry whose size or CRC-32 differs from what the archive declares.
/// Unix permission bits in the archive are kept so executables stay executable, except that setuid, setgid and sticky
/// are dropped and group and other never get write.
/// </summary>
internal static class SafeZipExtractor
{
    /// <summary>The most an archive may unpack to, unless the caller says otherwise: 512 MiB.</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    private const int MaxEntries = 10_000;
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSafeMode = 0x1ED; // rwx for the owner, r-x for group and other

    /// <summary>
    /// Unpacks <paramref name="zipPath"/> into <paramref name="destination"/>, which must not exist yet or be empty.
    /// Every entry is checked before anything is written.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive has an entry it refuses, or unpacks to more than
    /// <paramref name="maxBytes"/>. The message names the entry. Files already written stay; the caller deletes
    /// <paramref name="destination"/>.</exception>
    public static void Extract(string zipPath, string destination, long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
            throw new IOException($"'{destination}' already exists and is not empty.");

        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var plan = Validate(archive, rootWithSeparator, maxBytes);

        long written = 0;
        foreach (var (entry, target, isDirectory) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            written = WriteFile(entry, target, written, maxBytes, ct);
            ApplyUnixMode(entry, target);
        }
    }

    private static List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)> Validate(
        ZipArchive archive, string rootWithSeparator, long maxBytes)
    {
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException($"The archive has {archive.Entries.Count} entries; at most {MaxEntries} are allowed.");

        var plan = new List<(ZipArchiveEntry, string, bool)>(archive.Entries.Count);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // listed or implied by a longer path
        long declared = 0;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.Contains('\0') || name.Contains(':') || name[0] == '/' || name[0] == '\\')
                throw new InvalidDataException($"Refusing archive entry '{name}': its path is not allowed.");

            var segments = name.Split('/', '\\');
            if (segments.Contains(".."))
                throw new InvalidDataException($"Refusing archive entry '{name}': it climbs out of the folder.");

            var unixType = (entry.ExternalAttributes >> 16) & UnixTypeMask;
            var isDirectory = name[^1] is '/' or '\\' || unixType == UnixDirectory;
            if (unixType == 0xA000)
                throw new InvalidDataException($"Refusing archive entry '{name}': symbolic links are not allowed.");
            if (unixType is not (0 or UnixRegularFile or UnixDirectory))
                throw new InvalidDataException($"Refusing archive entry '{name}': it is not a regular file or folder.");

            var normalized = string.Join('/', segments.Where(s => s.Length > 0 && s != "."));
            if (normalized.Length == 0 && !isDirectory)
                throw new InvalidDataException($"Refusing archive entry '{name}': its path is not allowed.");
            if (normalized.Length > 0)
            {
                var parts = normalized.Split('/');
                var prefix = "";
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    prefix = i == 0 ? parts[0] : prefix + "/" + parts[i];
                    if (files.Contains(prefix))
                        throw new InvalidDataException($"Refusing archive entry '{name}': '{prefix}' is a file and a folder.");
                    folders.Add(prefix);
                }

                if (isDirectory)
                {
                    if (files.Contains(normalized))
                        throw new InvalidDataException($"Refusing archive entry '{name}': that path is a file and a folder.");
                    folders.Add(normalized);
                }
                else
                {
                    if (files.Contains(normalized))
                        throw new InvalidDataException($"Refusing archive entry '{name}': the archive has that path twice.");
                    if (folders.Contains(normalized))
                        throw new InvalidDataException($"Refusing archive entry '{name}': that path is a file and a folder.");
                    files.Add(normalized);
                }
            }

            // Only the declared sizes can be capped before writing; each entry is held to its declared size and CRC as it is written.
            declared += entry.Length;
            if (declared > maxBytes)
                throw new InvalidDataException($"Refusing archive entry '{name}': the archive unpacks to more than {maxBytes} bytes.");

            var target = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized));
            var contained = normalized.Length == 0
                ? target.TrimEnd(Path.DirectorySeparatorChar) == rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar)
                : target.StartsWith(rootWithSeparator, StringComparison.Ordinal);
            if (!contained)
                throw new InvalidDataException($"Refusing archive entry '{name}': it would be written outside the folder.");

            plan.Add((entry, target, isDirectory));
        }

        return plan;
    }

    private static long WriteFile(ZipArchiveEntry entry, string target, long written, long maxBytes, CancellationToken ct)
    {
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int read;
        long entryBytes = 0;
        var crc = 0xFFFFFFFFu;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            written += read;
            entryBytes += read;
            if (written > maxBytes)
                throw new InvalidDataException($"Refusing archive entry '{entry.FullName}': the archive unpacks to more than {maxBytes} bytes.");
            crc = UpdateCrc(crc, buffer, read);
            output.Write(buffer, 0, read);
        }

        if (entryBytes != entry.Length)
            throw new InvalidDataException($"Refusing archive entry '{entry.FullName}': it unpacked to {entryBytes} bytes, not the {entry.Length} it declares.");
        if (~crc != entry.Crc32)
            throw new InvalidDataException($"Refusing archive entry '{entry.FullName}': its contents do not match its CRC-32.");

        return written;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint UpdateCrc(uint crc, byte[] buffer, int count)
    {
        for (var i = 0; i < count; i++)
            crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void ApplyUnixMode(ZipArchiveEntry entry, string target)
    {
        if (OperatingSystem.IsWindows()) return;
        var attributes = entry.ExternalAttributes >> 16;
        if (attributes == 0) return;

        File.SetUnixFileMode(target, (UnixFileMode)(attributes & UnixSafeMode) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
