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
    /// <param name="Gid">The group that owns it.</param>
    internal readonly record struct Identity(ulong Device, ulong Inode, uint Uid, uint Gid);

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
                var gid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
                var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32));
                var major = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136));
                var minor = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
                return new Identity(((ulong)major << 32) | minor, inode, uid, gid);
            }

            if (OperatingSystem.IsMacOS())
            {
                var result = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? stat(path, ref buffer[0])
                    : stat_inode64(path, ref buffer[0]);
                if (result != 0)
                    return null;

                // struct stat with 64-bit inodes: st_dev i32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8, st_uid u32 @16, st_gid u32 @20.
                var device = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
                var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
                var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
                var gid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
                return new Identity(device, inode, uid, gid);
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static readonly object GroupLock = new();

    /// <summary>
    /// Whether <paramref name="gid"/> is this user's private group, the kind Ubuntu and others give every user: it is this
    /// process's effective group, is named after the user, and lists no member but the user. Anything else, or any
    /// failure to find out, is <see langword="false"/>.
    /// </summary>
    public static bool IsPrivateGroup(uint gid)
    {
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            if (getegid() != gid)
                return false;

            // getgrgid answers from one static buffer, so the call and the reads of it stay under a lock.
            lock (GroupLock)
            {
                var group = getgrgid(gid);
                if (group == IntPtr.Zero)
                    return false;

                // struct group on 64-bit Linux and macOS: gr_name @0, gr_passwd @8, gr_gid u32 @16, gr_mem (char**) @24.
                var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(group, 0));
                if (name is null || name != Environment.UserName)
                    return false;

                var members = Marshal.ReadIntPtr(group, 24);
                for (var i = 0; members != IntPtr.Zero; i++)
                {
                    var member = Marshal.ReadIntPtr(members, i * IntPtr.Size);
                    if (member == IntPtr.Zero)
                        return true;

                    if (Marshal.PtrToStringUTF8(member) != Environment.UserName)
                        return false;
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "getegid")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial uint getegid();

    [LibraryImport("libc", EntryPoint = "getgrgid")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial IntPtr getgrgid(uint gid);

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
