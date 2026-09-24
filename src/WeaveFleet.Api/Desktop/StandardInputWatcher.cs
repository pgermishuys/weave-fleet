using System.Runtime.InteropServices;

namespace WeaveFleet.Api.Desktop;

/// <summary>
/// Stops Fleet when its standard input closes. The desktop app starts Fleet with a pipe on standard input and
/// never writes to it. When the app exits, however it exits, the operating system closes the pipe and Fleet shuts
/// down with it, stopping its agents and releasing the database lock. Only runs in desktop mode: a Fleet started
/// with standard input on <c>/dev/null</c> would stop straight away.
/// </summary>
/// <remarks>
/// Nothing Fleet starts may share that pipe on Windows; <see cref="OpenAppInput"/> keeps it to the watcher.
/// </remarks>
internal sealed partial class StandardInputWatcher(
    Func<Stream> openInput,
    IHostApplicationLifetime lifetime,
    ILogger<StandardInputWatcher> logger) : BackgroundService
{
    /// <summary>
    /// Opens the app's pipe for the watcher and, on Windows, gives Fleet's standard input to <c>NUL</c> instead, so
    /// the processes Fleet starts don't inherit the pipe. The app's end of it is synchronous, and while the watcher's
    /// read is waiting Windows makes anyone else who asks about the pipe wait too, which is forever: git does exactly
    /// that as it starts, so every git Fleet ran hung, and the repository list never loaded (#301).
    /// </summary>
    public static Stream OpenAppInput()
    {
        // The stream keeps the pipe's handle, so it still reads the pipe once the standard handle points elsewhere.
        var input = Console.OpenStandardInput();
        if (OperatingSystem.IsWindows())
            HidePipeFromChildProcesses();
        return input;
    }

    private static void HidePipeFromChildProcesses()
    {
        var pipe = GetStdHandle(StdInputHandle);
        if (pipe == IntPtr.Zero || pipe == InvalidHandle)
            return;

        // Process.Start hands children Fleet's standard input when it doesn't redirect theirs, and CreateProcess
        // copies every inheritable handle into them besides; stop both.
        _ = SetHandleInformation(pipe, HandleFlagInherit, 0);
        var inheritable = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        var nul = CreateFile("NUL", GenericRead, FileShareRead | FileShareWrite, ref inheritable, OpenExisting, 0, IntPtr.Zero);
        if (nul != InvalidHandle)
            _ = SetStdHandle(StdInputHandle, nul);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var input = openInput();

        // Reads block, and a console stream can't cancel one, so the read gets its own thread and shutdown
        // stops waiting for it instead.
        var closed = Task.Factory.StartNew(
            () => ReadToEnd(input),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await closed.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        LogInputClosed();
        lifetime.StopApplication();
    }

    private static void ReadToEnd(Stream input)
    {
        var buffer = new byte[256];
        try
        {
            while (input.Read(buffer) > 0)
            {
            }
        }
        catch (IOException)
        {
            // A broken pipe is the app going away too.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop app closed Fleet's standard input — shutting down.")]
    private partial void LogInputClosed();

    private const int StdInputHandle = -10;
    private const uint HandleFlagInherit = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
