using System.Globalization;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>Ports a run's processes listen on: the app's, and its dev tools' (see <see cref="LinuxListeningPorts.ForRun"/>).</summary>
internal sealed record TreePorts(IReadOnlyList<int> App, IReadOnlyList<int> Helper)
{
    public static readonly TreePorts None = new([], []);
}

/// <summary>
/// The TCP ports a run's processes listen on, read from <c>/proc</c>: socket inodes from each process's
/// <c>fd</c> links, matched against the LISTEN rows of <c>/proc/net/tcp</c> and <c>tcp6</c>. Works for any
/// framework, since it doesn't depend on what the process prints. Empty on other platforms.
/// </summary>
internal static class LinuxListeningPorts
{
    /// <summary>
    /// The variable Fleet sets to the run's id. A process keeps the environment it started with, so this finds
    /// the run's processes that left its tree: an orchestrator that detaches (Aspire's DCP, which then starts
    /// the dashboard and every service) is reparented to init, but still carries it.
    /// </summary>
    public const string RunMarker = "FLEET_APP_RUN";

    private const string ListenState = "0A";

    /// <summary>
    /// The ports the run listens on: the process tree under <paramref name="rootPid"/>, and any process of this
    /// user that carries <see cref="RunMarker"/> set to <paramref name="runId"/>. Ports of dev tools that serve
    /// no page themselves (<c>dotnet watch</c>'s browser-refresh servers) come back as
    /// <see cref="TreePorts.Helper"/>, apart from the app's.
    /// </summary>
    public static TreePorts ForRun(int rootPid, string runId)
    {
        if (!OperatingSystem.IsLinux())
            return TreePorts.None;

        try
        {
            var inodes = new HashSet<long>();
            var helperInodes = new HashSet<long>();
            foreach (var pid in RunProcesses(rootPid, $"{RunMarker}={runId}"))
                AddSocketInodes(pid, IsHelper(pid) ? helperInodes : inodes);

            if (inodes.Count == 0 && helperInodes.Count == 0)
                return TreePorts.None;

            var listeners = ReadTable("/proc/net/tcp").Concat(ReadTable("/proc/net/tcp6")).ToList();
            return new TreePorts(PortsOf(listeners, inodes), PortsOf(listeners, helperInodes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TreePorts.None;
        }
    }

    /// <summary>Whether a process's command line (NULs as spaces) is a dev tool that listens for its own reasons, not the app.</summary>
    internal static bool IsHelperCommandLine(string cmdline)
        => cmdline.Contains("dotnet-watch", StringComparison.Ordinal);

    private static bool IsHelper(int pid)
    {
        try
        {
            return IsHelperCommandLine(File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<int> PortsOf(List<(int Port, long Inode)> listeners, HashSet<long> inodes)
        => [.. listeners.Where(listener => inodes.Contains(listener.Inode)).Select(listener => listener.Port).Distinct().Order()];

    /// <summary>LISTEN rows of a <c>/proc/net/tcp</c>-format table: the local port and the socket inode.</summary>
    internal static IEnumerable<(int Port, long Inode)> ParseListeners(string table)
    {
        foreach (var line in table.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || fields[3] != ListenState)
                continue;

            var local = fields[1];
            var colon = local.LastIndexOf(':');
            if (colon < 0
                || !int.TryParse(local.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
                || !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                continue;
            }

            yield return (port, inode);
        }
    }

    /// <summary>The parent pid from a <c>/proc/{pid}/stat</c> line. The name can hold spaces and parentheses, so fields count from the last ')'.</summary>
    internal static int? ParentPid(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) ? parent : null;
    }

    /// <summary>Whether a <c>/proc/{pid}/environ</c> (NUL-separated) holds exactly <paramref name="entry"/>.</summary>
    internal static bool HasEnvironmentEntry(string environ, string entry)
        => environ.Split('\0').Contains(entry, StringComparer.Ordinal);

    private static bool CarriesMarker(int pid, string entry)
    {
        try
        {
            return HasEnvironmentEntry(File.ReadAllText($"/proc/{pid}/environ"), entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Another user's process, or it ended while we looked.
        }
    }

    private static List<int> RunProcesses(int rootPid, string marker)
    {
        var pids = new List<int>();
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;

            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(dir, "stat"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // The process ended while we looked.
            }

            pids.Add(pid);
            if (ParentPid(stat) is { } parent)
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = [];
                list.Add(pid);
            }
        }

        var tree = new List<int> { rootPid };
        for (var i = 0; i < tree.Count; i++)
        {
            if (children.TryGetValue(tree[i], out var list))
                tree.AddRange(list);
        }

        var inTree = tree.ToHashSet();
        tree.AddRange(pids.Where(pid => !inTree.Contains(pid) && CarriesMarker(pid, marker)));
        return tree;
    }

    private static void AddSocketInodes(int pid, HashSet<long> inodes)
    {
        IEnumerable<string> fds;
        try
        {
            fds = Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var fd in fds)
        {
            string? target;
            try
            {
                target = new FileInfo(fd).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // "socket:[123456]"
            if (target is { Length: > 9 }
                && target.StartsWith("socket:[", StringComparison.Ordinal)
                && long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                inodes.Add(inode);
            }
        }
    }

    private static List<(int Port, long Inode)> ReadTable(string path)
    {
        try
        {
            return ParseListeners(File.ReadAllText(path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
