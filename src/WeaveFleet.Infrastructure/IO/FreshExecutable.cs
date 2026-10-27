using System.ComponentModel;
using System.Diagnostics;

namespace WeaveFleet.Infrastructure.IO;

/// <summary>
/// Starts an executable Fleet's own process may have just written, such as a Bun or Copilot it unpacked.
/// <para>
/// On Linux and macOS the kernel won't exec a file while anything holds it open for writing ("Text file busy",
/// <c>ETXTBSY</c>). Fleet closes its handle before starting the file, but a process another thread started while the
/// handle was open was forked with a copy of it, and holds that copy until it execs a moment later. .NET opens files
/// close-on-exec, so the copy can't outlive that moment, but no write path (a temporary file renamed into place
/// included) can keep it out of a fork that happens mid-write. So the start is retried for a moment instead, which
/// works whoever forked.
/// </para>
/// </summary>
internal static class FreshExecutable
{
    /// <summary><c>ETXTBSY</c>, the same number on Linux and macOS.</summary>
    internal const int TextFileBusy = 26;

    /// <summary>Tries after the first, waiting 5, 10, 20, 40, 80 and 160 ms: about a third of a second in all.</summary>
    private const int Retries = 6;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>Starts <paramref name="startInfo"/>. Throws like <see cref="Process.Start(ProcessStartInfo)"/> when it can't.</summary>
    public static Process Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            Start(process);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Starts <paramref name="process"/>. Throws like <see cref="Process.Start()"/> when it can't.</summary>
    public static void Start(Process process)
    {
        var delay = FirstDelay;
        for (var retry = 0; ; retry++)
        {
            try
            {
                process.Start();
                return;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == TextFileBusy && retry < Retries && !OperatingSystem.IsWindows())
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }
}
