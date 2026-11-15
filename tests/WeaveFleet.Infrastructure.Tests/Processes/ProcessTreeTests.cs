using System.Diagnostics;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Processes;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Processes;

/// <summary>
/// Killing a process and everything it started without stopping any of them (see <see cref="ProcessTree"/>). The ones
/// that start processes run on Linux and, in CI's "Mod file safety (macos-latest)" job, on a Mac.
/// </summary>
[Trait("Category", "ProcessTree")]
public sealed partial class ProcessTreeTests : IDisposable
{
    // A root, a child that's sleep, a child that's a shell, and that shell's sleep: each writes its pid to pids.
    private const string TreeScript = """
        echo $$ >> "$1/pids"
        sh -c 'echo $$ >> "$0/pids"; exec sleep 300' "$1" &
        sh -c 'echo $$ >> "$0/pids"; sh -c "echo \$\$ >> \"\$0/pids\"; exec sleep 300" "$0" & wait' "$1" &
        wait
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"fleet-tree-{Guid.NewGuid():N}");

    public ProcessTreeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        foreach (var pid in RecordedPids())
        {
            try { using var leftover = Process.GetProcessById(pid); leftover.Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_process_and_every_process_it_started_are_killed()
    {
        if (!ProcessIdentity.IsSupported)
            return;
        File.WriteAllText(At("tree.sh"), TreeScript);
        using var root = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { At("tree.sh"), _folder } })!;
        var pids = await WaitForPidsAsync(4);

        ProcessTree.Kill(root);

        foreach (var pid in pids)
            (await GoneAsync(pid)).ShouldBeTrue($"process {pid} of the tree is still running");
        root.WaitForExit(10_000).ShouldBeTrue("the root wasn't killed"); // .NET reaps it a moment after it dies
    }

    [Fact]
    public void A_process_that_has_exited_is_left_alone()
    {
        if (!ProcessIdentity.IsSupported)
            return;
        using var done = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" } })!;
        done.WaitForExit();

        Should.NotThrow(() => ProcessTree.Kill(done));
    }

    [Fact]
    public void A_Process_with_no_process_behind_it_throws_like_dotnets_own_tree_kill()
        => Should.Throw<InvalidOperationException>(() => ProcessTree.Kill(new Process()));

    /// <summary>
    /// The way the desktop app runs Fleet: launchd starts the app, the app starts Fleet, so Fleet's process group's
    /// leader has launchd for a parent. Killing a tree there with .NET's own <c>Kill(entireProcessTree: true)</c> sent
    /// the whole group SIGHUP and SIGCONT and ended Fleet (scratch run 43662529461, job "macos-latest launchd").
    /// This test makes such a group (a job submitted to launchd that logs every SIGHUP it gets), kills a tree in it
    /// and checks nobody got SIGHUP.
    /// </summary>
    [Fact]
    public async Task On_a_Mac_killing_a_tree_in_a_group_launchd_started_sends_the_group_no_SIGHUP()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        File.WriteAllText(At("tree.sh"), TreeScript);
        File.WriteAllText(At("job.sh"), """
            trap 'echo HUP >> "$1/signals"' HUP
            trap 'echo CONT >> "$1/signals"' CONT
            echo $$ > "$1/job"
            /bin/sh "$1/tree.sh" "$1" &
            while :; do sleep 1; done
            """);
        var label = $"io.tryweave.fleet-test.{Guid.NewGuid():N}";
        if (await LaunchctlAsync("submit", "-l", label, "--", "/bin/bash", At("job.sh"), _folder) != 0)
            return; // No launchd domain to submit to (not logged in): nothing to check here.

        try
        {
            var pids = await WaitForPidsAsync(4);
            var job = int.Parse(await WaitForFileAsync("job"), System.Globalization.CultureInfo.InvariantCulture);
            using var root = Process.GetProcessById(pids[0]);

            ProcessTree.Kill(root);

            foreach (var pid in pids)
                (await GoneAsync(pid)).ShouldBeTrue($"process {pid} of the tree is still running");
            await Task.Delay(TimeSpan.FromSeconds(2)); // the kernel's signals arrive straight away; give the traps time
            var signals = File.Exists(At("signals")) ? await File.ReadAllTextAsync(At("signals")) : "";
            signals.ShouldNotContain("HUP", customMessage: "the tree's process group got SIGHUP: something in the tree was stopped");
            ProcessIdentity.Read(job).ShouldNotBeNull("the job that started the tree didn't survive");
        }
        finally
        {
            await LaunchctlAsync("remove", label);
        }
    }

    [Fact]
    public void Descendants_come_parents_first_without_the_ones_already_seen_or_a_loop()
    {
        var children = new Dictionary<int, IReadOnlyList<int>>
        {
            [10] = [11, 12],
            [11] = [13],
            [12] = [14, 10], // a pid reused by an ancestor isn't followed back
            [13] = [],
            [14] = [0],      // pid 0 is nobody's process
        };
        var seen = new HashSet<int> { 10 };

        ProcessTree.Descendants([10], pid => children.GetValueOrDefault(pid, []), seen).ShouldBe([11, 12, 13, 14]);
        ProcessTree.Descendants([11, 12], pid => children.GetValueOrDefault(pid, []), seen).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1234 (sleep) S 1 1234 1234 0 -1 4194560", 1)]
    [InlineData("1234 (a b) (c) R 987 1234 1234 0 -1", 987)]
    [InlineData("1234 (worker)) S 55 1 1", 55)]
    [InlineData("garbage", null)]
    [InlineData("1234 (x)", null)]
    public void The_parent_is_read_from_a_proc_stat_line_after_the_name(string stat, int? parent)
        => ProcessTree.ParentInLinuxStat(stat).ShouldBe(parent);

    [Fact]
    public void Linux_processes_are_listed_by_parent_from_proc()
    {
        var proc = Directory.CreateDirectory(At("proc")).FullName;
        foreach (var (pid, line) in new[] { ("1", "1 (init) S 0 1 1"), ("20", "20 (sh) S 1 20 20"), ("21", "21 (sleep 3) S 20 20 20"), ("22", "22 (sh) S 20 20 20") })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(proc, pid)).FullName, "stat"), line);
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        Directory.CreateDirectory(Path.Combine(proc, "30")); // gone while being read: no stat

        var byParent = ProcessTree.LinuxChildrenByParent(proc);

        byParent[20].Order().ShouldBe([21, 22]);
        byParent[1].ShouldBe([20]);
        byParent[0].ShouldBe([1]);
        byParent.Keys.Count.ShouldBe(3);
    }

    /// <summary>
    /// .NET's own tree kill stops each process before it kills it, and on a Mac that SIGHUPs Fleet's process group
    /// (see <see cref="ProcessTree"/>). Everything in Fleet goes through <see cref="ProcessTree.Kill"/> instead.
    /// </summary>
    [Fact]
    public void Nothing_in_Fleet_kills_a_tree_with_dotnets_own_tree_kill()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var allowed = Path.Combine(src, "WeaveFleet.Application", "Processes", "ProcessTree.cs");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && file != allowed)
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(hit => TreeKill().IsMatch(hit.line))
            .Select(hit => $"{Path.GetRelativePath(src, hit.file)}:{hit.index + 1}: {hit.line.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty("call ProcessTree.Kill(process) instead");
    }

    [GeneratedRegex(@"\.Kill\(\s*(entireProcessTree\s*:\s*)?true\s*\)")]
    private static partial Regex TreeKill();

    private string At(string name) => Path.Combine(_folder, name);

    private List<int> RecordedPids()
    {
        var file = At("pids");
        if (!File.Exists(file))
            return [];
        return File.ReadAllLines(file).Select(line => int.TryParse(line, out var pid) ? pid : 0).Where(pid => pid > 0).ToList();
    }

    private async Task<List<int>> WaitForPidsAsync(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            var pids = RecordedPids();
            if (pids.Count >= count)
            {
                await Task.Delay(300); // each one past its echo, into sleep or wait
                return pids;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The tree didn't start: {string.Join(", ", RecordedPids())}");
    }

    private async Task<string> WaitForFileAsync(string name)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(At(name)) && (await File.ReadAllTextAsync(At(name))).Trim() is { Length: > 0 } text)
                return text;
            await Task.Delay(50);
        }

        throw new TimeoutException($"{name} wasn't written");
    }

    // Killed processes pass to init or launchd, which reaps them soon after.
    private static async Task<bool> GoneAsync(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            if (ProcessIdentity.Read(pid) is null)
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<int> LaunchctlAsync(params string[] args)
    {
        var start = new ProcessStartInfo("/bin/launchctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var launchctl = Process.Start(start)!;
        await launchctl.WaitForExitAsync();
        return launchctl.ExitCode;
    }

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "WeaveFleet.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Can't find the repository root.");
    }
}
