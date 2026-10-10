using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WeaveFleet.Infrastructure.IO;

namespace WeaveFleet.Infrastructure.Mods;

/// <summary>Why a file under a draft or a version can't be read.</summary>
internal enum FileProblem
{
    None,

    /// <summary>It isn't there (any more).</summary>
    Missing,

    /// <summary>The name is a symbolic link.</summary>
    Link,

    /// <summary>A folder, a named pipe, a socket or a device.</summary>
    NotRegular,

    /// <summary>The OS refused to open it (no permission).</summary>
    Unreadable,

    /// <summary>Bigger than the cap the caller gave, by the size the OS reports.</summary>
    TooLarge,

    /// <summary>This OS or CPU isn't one Fleet ships for, so a file can't be checked safely; nothing is opened.</summary>
    Unsupported,
}

/// <summary>
/// The one way the mod store reads a file under a draft or a version: it opens the file and finds out what it is in the
/// same step, so there is no gap for a swap to a named pipe to slip into. On Unix the file is opened with
/// <c>O_NONBLOCK</c> (an open can never wait for a writer) and <c>O_NOFOLLOW</c> (a link is refused), then <c>fstat</c> on that
/// very handle must say "regular file", and its size must fit the caller's cap before anything is read. On Windows the
/// attributes are checked and the file is opened with <see cref="FileShare.Read"/>; named pipes don't exist there as files.
/// </summary>
internal static partial class SafeFile
{
    // NativeFileStatus (SystemNative_FStat) normalises the mode bits on every OS (PAL_S_IFMT 0xF000, IFIFO 0x1000, IFCHR 0x2000,
    // IFDIR 0x4000, IFREG 0x8000, IFLNK 0xA000, IFSOCK 0xC000).
    private const int FormatMask = 0xF000;
    private const int RegularFile = 0x8000;

    private const int Enoent = 2;
    private const int Eacces = 13;
    private const int ElinuxLoop = 40;
    private const int EmacLoop = 62;

    // Linux x64 (glibc and musl), Linux arm64, and macOS. Fleet ships no other Unix.
    private static readonly UnixLayout? Layout = UnixLayout.ForThisSystem();

    /// <summary>The <c>open</c> flags and the "too many links" error, which differ by OS and CPU. Nothing else does.</summary>
    private sealed record UnixLayout(int OpenFlags, int LoopError)
    {
        private const int OpenReadOnly = 0;
        private const int NonBlock = 0x800;
        private const int CloseOnExec = 0x80000;

        public static UnixLayout? ForThisSystem()
        {
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            if (OperatingSystem.IsLinux() && x64)
                return new UnixLayout(OpenReadOnly | NonBlock | 0x20000 | CloseOnExec, ElinuxLoop);
            if (OperatingSystem.IsLinux() && arm)
                return new UnixLayout(OpenReadOnly | NonBlock | 0x8000 | CloseOnExec, ElinuxLoop);
            if (OperatingSystem.IsMacOS() && (x64 || arm))
                return new UnixLayout(OpenReadOnly | 0x4 | 0x100 | 0x1000000, EmacLoop);
            return null;
        }
    }

    // open stays on libc: SystemNative_Open takes PAL flags (pal_io.h PAL_O_RDONLY/CLOEXEC/CREAT/EXCL/TRUNC/SYNC/NOFOLLOW)
    // and has no O_NONBLOCK, which is what stops a named pipe from blocking the open.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeOpen(string path, int flags);

    /// <summary>
    /// Opens <paramref name="path"/> for reading when it is a regular file of at most <paramref name="maxBytes"/> (by the size
    /// the OS reports); otherwise says why not and opens nothing. The caller disposes the stream.
    /// </summary>
    public static FileProblem Open(string path, long maxBytes, out FileStream? stream)
    {
        stream = null;
        try
        {
            return OperatingSystem.IsWindows() ? OpenWindows(path, maxBytes, out stream) : OpenUnix(path, maxBytes, out stream);
        }
        catch (DllNotFoundException)
        {
            return FileProblem.Unsupported;
        }
        catch (EntryPointNotFoundException)
        {
            return FileProblem.Unsupported;
        }
    }

    /// <summary>All of a regular file's bytes, when it is one of at most <paramref name="maxBytes"/> (also while reading).</summary>
    public static FileProblem ReadAll(string path, long maxBytes, out byte[] data)
    {
        data = [];
        var problem = Open(path, maxBytes, out var stream);
        if (problem != FileProblem.None)
            return problem;

        using (stream)
        {
            using var collected = new MemoryStream();
            var buffer = new byte[Math.Min(81920, Math.Max(1, maxBytes + 1))];
            int read;
            while ((read = stream!.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (collected.Length + read > maxBytes)
                    return FileProblem.TooLarge;
                collected.Write(buffer, 0, read);
            }

            data = collected.ToArray();
            return FileProblem.None;
        }
    }

    /// <summary>The SHA-256 (hex) and the length of a regular file of at most <paramref name="maxBytes"/>.</summary>
    public static FileProblem Hash(string path, long maxBytes, out string sha256, out long length)
    {
        sha256 = "";
        length = 0;
        var problem = Open(path, maxBytes, out var stream);
        if (problem != FileProblem.None)
            return problem;

        using (stream)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = stream!.Read(buffer, 0, buffer.Length)) > 0)
            {
                length += read;
                if (length > maxBytes)
                    return FileProblem.TooLarge;
                hash.AppendData(buffer, 0, read);
            }

            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            return FileProblem.None;
        }
    }

    private static FileProblem OpenWindows(string path, long maxBytes, out FileStream? stream)
    {
        stream = null;
        var info = new FileInfo(path);
        if (!info.Exists)
            return Directory.Exists(path) ? FileProblem.NotRegular : FileProblem.Missing;
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return FileProblem.Link;
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            return FileProblem.NotRegular;

        try
        {
            var opened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (opened.Length > maxBytes)
            {
                opened.Dispose();
                return FileProblem.TooLarge;
            }

            stream = opened;
            return FileProblem.None;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileProblem.Missing;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return FileProblem.Unreadable;
        }
    }

    private static FileProblem OpenUnix(string path, long maxBytes, out FileStream? stream)
    {
        stream = null;
        if (Layout is not { } layout)
            return FileProblem.Unsupported;

        var fd = NativeOpen(path, layout.OpenFlags);
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Enoent ? FileProblem.Missing
                : error == layout.LoopError ? FileProblem.Link
                : FileProblem.Unreadable;
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            if (!NativeFileStatus.TryFStat(fd, out var status))
                return Marshal.GetLastPInvokeError() == Eacces ? FileProblem.Unreadable : FileProblem.NotRegular;

            if ((status.Mode & FormatMask) != RegularFile)
                return FileProblem.NotRegular;
            if (status.Size > maxBytes)
                return FileProblem.TooLarge;

            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            return FileProblem.None;
        }
        finally
        {
            if (stream is null)
                handle.Dispose();
        }
    }
}
