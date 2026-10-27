using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.IO;

/// <summary>
/// Whether the read end of a pipe has something to read, which .NET's public API doesn't say. On Linux and macOS it
/// calls .NET's own <c>libSystem.Native</c> (<c>SystemNative_Poll</c>), whose <c>PollEvent</c> layout and flags are the
/// same on every OS and CPU; on Windows, <c>PeekNamedPipe</c>.
/// </summary>
internal static partial class PipeInput
{
    // PAL_POLLIN, PAL_POLLERR, PAL_POLLHUP and PAL_POLLNVAL from dotnet/runtime src/native/libs/System.Native/pal_io.h.
    private const short PollIn = 0x0001;
    private const short Readable = PollIn | 0x0008 | 0x0010 | 0x0020;

    /// <summary><c>PollEvent</c> from pal_io.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PollEvent
    {
        public int FileDescriptor;
        public short Events;
        public short TriggeredEvents;
    }

    // Error SystemNative_Poll(PollEvent* pollEvents, uint32_t eventCount, int32_t milliseconds, uint32_t* triggered); 0 is success.
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Poll")]
    private static unsafe partial int NativePoll(PollEvent* pollEvents, uint eventCount, int milliseconds, uint* triggered);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(SafeHandle pipe, nint buffer, uint bufferSize, nint bytesRead, out uint totalBytesAvailable, nint bytesLeftThisMessage);

    /// <summary>
    /// Whether a read of <paramref name="pipe"/> would return at once, with data, the end of the pipe or an error,
    /// waiting up to <paramref name="wait"/> for that. <see langword="true"/> when it can't tell, so a caller that then
    /// reads can block but never misses anything.
    /// </summary>
    public static bool HasInput(SafeHandle pipe, TimeSpan wait)
    {
        try
        {
            return OperatingSystem.IsWindows() ? HasInputWindows(pipe, wait) : HasInputUnix(pipe, wait);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static unsafe bool HasInputUnix(SafeHandle pipe, TimeSpan wait)
    {
        var added = false;
        try
        {
            pipe.DangerousAddRef(ref added);
            var poll = new PollEvent { FileDescriptor = (int)pipe.DangerousGetHandle(), Events = PollIn };
            uint triggered;
            var error = NativePoll(&poll, 1, (int)wait.TotalMilliseconds, &triggered);
            return error != 0 || (triggered > 0 && (poll.TriggeredEvents & Readable) != 0);
        }
        finally
        {
            if (added)
                pipe.DangerousRelease();
        }
    }

    private static bool HasInputWindows(SafeHandle pipe, TimeSpan wait)
    {
        // A pipe whose writers have all closed fails with ERROR_BROKEN_PIPE, and a read then returns its end.
        if (!PeekNamedPipe(pipe, 0, 0, 0, out var available, 0) || available > 0)
            return true;

        Thread.Sleep(wait);
        return PeekNamedPipe(pipe, 0, 0, 0, out available, 0) is false || available > 0;
    }
}
