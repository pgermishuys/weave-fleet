using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WeaveFleet.Application.Processes;

/// <summary>
/// Kills a process and everything it started, the way <c>Process.Kill(entireProcessTree: true)</c> does, except that on
/// Linux and macOS it never stops (<c>SIGSTOP</c>) any of them first.
/// <para>
/// .NET stops each process of the tree, then kills it, so it can't start more. On a Mac that can end Fleet: the killed
/// process's children pass to launchd while they're stopped, and macOS then sends <c>SIGHUP</c> and <c>SIGCONT</c> to
/// their whole process group (the orphaned process group rule). On Linux and macOS the processes Fleet starts share
/// Fleet's process group (.NET can't start one in a group of its own there: <c>CreateNewProcessGroup</c> is Windows only),
/// so that <c>SIGHUP</c> reaches Fleet itself, whatever started it (the desktop app) and everything else in the group.
/// On GitHub's Macs it killed the runner (#1015). Here every process of the tree gets <c>SIGKILL</c> at once; with
/// nothing stopped, the kernel has nobody to send <c>SIGHUP</c> for.
/// </para>
/// <para>Windows has no such signals and keeps .NET's own tree kill.</para>
/// </summary>
internal static partial class ProcessTree
{
    private const int SigKill = 9;

    // A process that forks while the tree is being killed leaves a child behind; another look catches it, as long as
    // its parent hasn't gone yet.
    private const int Passes = 3;

    /// <summary>
    /// Kills <paramref name="process"/> and its descendants straight away. Does nothing when it has exited already.
    /// Throws, like <c>Process.Kill(entireProcessTree: true)</c>, <see cref="InvalidOperationException"/> when no process
    /// is associated with <paramref name="process"/> or the tree holds Fleet's own process, and
    /// <see cref="AggregateException"/> when <paramref name="process"/> itself can't be killed.
    /// </summary>
    public static void Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            process.Kill(entireProcessTree: true);
            return;
        }

        // Before anything else: an exited process's pid may belong to another process by now.
        if (process.HasExited)
            return;

        var root = process.Id;
        var children = ChildrenReader();
        var tree = Descendants([root], children, new HashSet<int> { root });
        if (tree.Contains(Environment.ProcessId))
            throw new InvalidOperationException("The process tree to kill holds Fleet's own process.");

        // Through the Process object, which knows whether its process has exited (and its pid been reused).
        try
        {
            process.Kill();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AggregateException("Not every process of the tree could be killed.", ex);
        }

        var killed = new HashSet<int> { root };
        for (var pass = 0; pass < Passes && tree.Count > 0; pass++)
        {
            foreach (var pid in tree)
            {
                _ = Signal(pid, SigKill); // gone already, or not ours: nothing more to do
                killed.Add(pid);
            }

            // Whatever a process of the tree started before it died.
            tree = Descendants(tree, ChildrenReader(), killed);
        }
    }

    /// <summary>
    /// The descendants of <paramref name="parents"/>, parents before their children, leaving out
    /// <paramref name="seen"/> (and adding each one found to it).
    /// </summary>
    internal static List<int> Descendants(IEnumerable<int> parents, Func<int, IReadOnlyList<int>> children, HashSet<int> seen)
    {
        var found = new List<int>();
        var next = new Queue<int>(parents);
        while (next.TryDequeue(out var parent))
        {
            foreach (var child in children(parent))
            {
                if (child > 0 && seen.Add(child))
                {
                    found.Add(child);
                    next.Enqueue(child);
                }
            }
        }

        return found;
    }

    /// <summary>The children of each process, as they are now.</summary>
    private static Func<int, IReadOnlyList<int>> ChildrenReader()
    {
        if (OperatingSystem.IsMacOS())
            return MacChildren;

        var byParent = LinuxChildrenByParent("/proc");
        return pid => byParent.TryGetValue(pid, out var list) ? list : [];
    }

    /// <summary>Every process in <paramref name="procRoot"/> (<c>/proc</c>) by its parent's pid.</summary>
    internal static Dictionary<int, List<int>> LinuxChildrenByParent(string procRoot)
    {
        var byParent = new Dictionary<int, List<int>>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(procRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return byParent;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;

            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(entry, "stat"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // Gone since, or not ours to read.
            }

            if (ParentInLinuxStat(stat) is { } parent)
            {
                if (!byParent.TryGetValue(parent, out var list))
                    byParent[parent] = list = [];
                list.Add(pid);
            }
        }

        return byParent;
    }

    /// <summary>
    /// The parent pid in a <c>/proc/&lt;pid&gt;/stat</c> line, <c>pid (comm) state ppid …</c>. The name can hold spaces
    /// and parentheses, so the fields are counted from its last <c>)</c>.
    /// </summary>
    internal static int? ParentInLinuxStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            ? parent
            : null;
    }

    [SupportedOSPlatform("macos")]
    private static List<int> MacChildren(int pid)
    {
        // proc_listchildpids fills the buffer from the start; pid 0 is nobody's child, so the first 0 ends the list
        // (whether it returns a count or a size in bytes). A full buffer may have left some out: try a bigger one.
        for (var size = 256; ; size *= 4)
        {
            var buffer = new int[size];
            var result = ListChildPids(pid, buffer, size * sizeof(int));
            if (result <= 0)
                return [];

            if (buffer[^1] == 0 || size >= 65536)
                return [.. buffer.TakeWhile(child => child != 0)];
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int pid, int signal);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libproc", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static partial int ListChildPids(int ppid, [Out] int[] buffer, int bufferSize);
}
