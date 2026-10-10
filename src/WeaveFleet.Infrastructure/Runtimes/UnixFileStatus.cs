using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// The owner and identity (device and inode) of a file on Linux and macOS, which .NET's public API doesn't give.
/// Calls libc directly: <c>statx</c> on Linux, <c>stat</c> on macOS. Not for Windows, where it always answers
/// <see langword="null"/>.
/// </summary>
internal static partial class UnixFileStatus
{
    /// <summary>Big enough for <c>struct statx</c> (256 bytes) and <c>struct stat</c> on either OS.</summary>
    private const int BufferSize = 512;

    private const int AtFdCwd = -100;
    private const uint StatxBasicStats = 0x7ff;

    /// <summary>What a file is: where it lives and who owns it.</summary>
    /// <param name="Device">The device it's on.</param>
    /// <param name="Inode">Its inode number on that device.</param>
    /// <param name="Uid">The user that owns it.</param>
    internal readonly record struct Identity(ulong Device, ulong Inode, uint Uid);

    /// <summary>The effective user id of this process, or <see langword="null"/> on Windows or if it can't be asked.</summary>
    public static uint? EffectiveUserId()
    {
        if (OperatingSystem.IsWindows())
            return null;

        try
        {
            return geteuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks for <paramref name="path"/>'s device, inode and owner, following links. <see langword="null"/> when the file
    /// can't be examined or this isn't Linux or macOS; callers treat that as "can't tell" and fail closed.
    /// </summary>
    public static Identity? Stat(string path)
    {
        try
        {
            var buffer = new byte[BufferSize];
            if (OperatingSystem.IsLinux())
            {
                if (statx(AtFdCwd, path, 0, StatxBasicStats, ref buffer[0]) != 0)
                    return null;

                // struct statx is laid out the same on every Linux architecture (linux/stat.h).
                var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
                var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32));
                var major = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136));
                var minor = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
                return new Identity(((ulong)major << 32) | minor, inode, uid);
            }

            if (OperatingSystem.IsMacOS())
            {
                var result = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? stat(path, ref buffer[0])
                    : stat_inode64(path, ref buffer[0]);
                if (result != 0)
                    return null;

                // struct stat with 64-bit inodes: st_dev i32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8, st_uid u32 @16.
                var device = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
                var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
                var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
                return new Identity(device, inode, uid);
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int statx(int dirFd, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int stat(string path, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int stat_inode64(string path, ref byte buffer);
}
