using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.IO;

/// <summary>What the OS says about a file: its type and permission bits, owner, size and identity.</summary>
/// <param name="Mode">The PAL mode: permission bits (0xFFF) and the type bits (0xF000: FIFO 0x1000, char device 0x2000, folder 0x4000, regular 0x8000, link 0xA000, socket 0xC000), the same on every OS.</param>
/// <param name="Uid">The user that owns it.</param>
/// <param name="Gid">The group that owns it.</param>
/// <param name="Size">Its size in bytes, as the OS reports it.</param>
/// <param name="Dev">The device it's on, as one number (the OS's own encoding of major and minor).</param>
/// <param name="Ino">Its inode number on that device.</param>
internal readonly record struct FileStatusInfo(int Mode, uint Uid, uint Gid, long Size, ulong Dev, ulong Ino);

/// <summary>
/// The one place Fleet asks the OS about a file's mode, owner, size, device and inode on Linux and macOS, which .NET's
/// public API doesn't give. It calls .NET's own <c>libSystem.Native</c> (<c>SystemNative_Stat</c>, <c>LStat</c>, <c>FStat</c>,
/// <c>GetEUid</c>, <c>GetEGid</c>), whose <c>FileStatus</c> layout is the same on every OS and CPU and which is linked
/// statically under Native AOT. Nothing here is a hand-written libc layout. Not for Windows, where every call fails.
/// Every failure, including a missing library or entry point, is <see langword="false"/> or <see langword="null"/>:
/// callers treat that as "can't tell" and fail closed.
/// </summary>
internal static partial class NativeFileStatus
{
    // The whole FileStatus is 17 fields (about 160 bytes today). The native side gets far more room than that, so a
    // field appended at the end by a later .NET can never write past our buffer.
    private const int StatBufferBytes = 256;

    /// <summary>
    /// <c>FileStatus</c> from dotnet/runtime release/10.0 <c>src/native/libs/System.Native/pal_io.h</c>, all 17 fields in
    /// order. Its layout is the same on every OS and CPU; fields are only ever appended. The BCL's own copy is Interop.Stat.cs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime;
        public long ATimeNsec;
        public long MTime;
        public long MTimeNsec;
        public long CTime;
        public long CTimeNsec;
        public long BirthTime;
        public long BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;
    }

    // int32_t SystemNative_Stat(const char* path, FileStatus* output); returns 0, or -1 with errno set.
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStat(string path, Span<byte> output);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeLStat(string path, Span<byte> output);

    // int32_t SystemNative_FStat(intptr_t fd, FileStatus* output)
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static partial int NativeFStat(nint fd, Span<byte> output);

    // uint32_t SystemNative_GetEUid(void) and SystemNative_GetEGid(void), from pal_uid.h.
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
    private static partial uint NativeGetEUid();

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_GetEGid")]
    private static partial uint NativeGetEGid();

    /// <summary><c>stat</c>: the status of <paramref name="path"/>, following links. False when it can't be examined.</summary>
    public static bool TryStat(string path, out FileStatusInfo info) => Try(buffer => NativeStat(path, buffer), out info);

    /// <summary><c>lstat</c>: the status of <paramref name="path"/> itself, so a link is reported as a link.</summary>
    public static bool TryLStat(string path, out FileStatusInfo info) => Try(buffer => NativeLStat(path, buffer), out info);

    /// <summary><c>fstat</c>: the status of an open file, by its descriptor. After a failure, <see cref="Marshal.GetLastPInvokeError"/> has the errno.</summary>
    public static bool TryFStat(nint fd, out FileStatusInfo info) => Try(buffer => NativeFStat(fd, buffer), out info);

    /// <summary>The effective user id of this process; <see langword="null"/> on Windows or if it can't be asked.</summary>
    public static uint? EffectiveUserId() => Ask(NativeGetEUid);

    /// <summary>The effective group id of this process; <see langword="null"/> on Windows or if it can't be asked.</summary>
    public static uint? EffectiveGroupId() => Ask(NativeGetEGid);

    private delegate int StatCall(Span<byte> buffer);

    private static bool Try(StatCall call, out FileStatusInfo info)
    {
        info = default;
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            Span<byte> buffer = stackalloc byte[StatBufferBytes];
            buffer.Clear();
            if (call(buffer) != 0)
                return false;

            var status = MemoryMarshal.Read<FileStatus>(buffer);
            info = new FileStatusInfo(status.Mode, status.Uid, status.Gid, status.Size, unchecked((ulong)status.Dev), unchecked((ulong)status.Ino));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static uint? Ask(Func<uint> call)
    {
        if (OperatingSystem.IsWindows())
            return null;

        try
        {
            return call();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
