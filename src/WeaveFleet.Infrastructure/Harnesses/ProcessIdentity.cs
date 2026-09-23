using System.Diagnostics;
using System.Globalization;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// One process, told apart from any later process that gets the same pid: its pid, when it started (clock ticks
/// since boot on Linux, from <c>/proc/&lt;pid&gt;/stat</c>; the start time the kernel keeps on macOS) and, on Linux,
/// which boot it's from. A pid is reused only once its process is gone, and the new one starts later, so two
/// processes never share all three. <see cref="Name"/> is for logs only: a process can rename itself.
/// </summary>
internal sealed record ProcessIdentity(int Pid, long StartTime, string? BootId, string Name)
{
    /// <summary>Whether this is the same process as <paramref name="other"/> (the name aside).</summary>
    public bool IsSameProcess(ProcessIdentity? other)
        => other is not null && other.Pid == Pid && other.StartTime == StartTime
           && string.Equals(other.BootId, BootId, StringComparison.Ordinal);

    /// <summary>Whether Fleet can tell processes apart on this platform (Linux and macOS).</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static readonly Lazy<string?> CurrentBootId = new(ReadBootId);

    /// <summary>The running process with pid <paramref name="pid"/>, or <see langword="null"/> when there's none.</summary>
    public static ProcessIdentity? Read(int pid)
    {
        if (pid <= 0)
            return null;

        try
        {
            if (OperatingSystem.IsLinux())
            {
                return ParseLinuxStat(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"), CurrentBootId.Value);
            }

            if (OperatingSystem.IsMacOS())
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return null;
                return new ProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks, null, process.ProcessName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone, or not ours to read.
        }

        return null;
    }

    /// <summary>
    /// Reads <c>/proc/&lt;pid&gt;/stat</c>: <c>pid (comm) state ppid …</c>, where the start time is the 22nd field.
    /// The name can hold spaces and parentheses, so the fields after it are counted from its last <c>)</c>.
    /// A zombie (state <c>Z</c>) has exited already and counts as gone.
    /// </summary>
    internal static ProcessIdentity? ParseLinuxStat(string stat, string? bootId)
    {
        var open = stat.IndexOf('(', StringComparison.Ordinal);
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open)
            return null;

        if (!int.TryParse(stat.AsSpan(0, open).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            return null;

        // Field 3 (state) is the first after the name, so field 22 (starttime) is the 20th.
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0] == "Z"
            || !long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTime))
        {
            return null;
        }

        return new ProcessIdentity(pid, startTime, bootId, stat[(open + 1)..close]);
    }

    private static string? ReadBootId()
    {
        try
        {
            return OperatingSystem.IsLinux() ? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
