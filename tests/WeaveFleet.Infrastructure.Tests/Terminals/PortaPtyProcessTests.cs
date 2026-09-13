using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Infrastructure.Terminals;

namespace WeaveFleet.Infrastructure.Tests.Terminals;

/// <summary>
/// Runs a real shell on a real pseudoterminal. Linux only for now: macOS and Windows are covered by the
/// checklist in the terminal plan until CI runs there.
/// </summary>
public sealed class PortaPtyProcessTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _workDir = Directory.CreateTempSubdirectory("fleet-pty-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Spawn_StartsInTheWorkingDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir);
        await shell.SendAsync("pwd\r");

        (await shell.WaitForAsync(Regex.Escape(_workDir) + @"\r?\n")).ShouldBeTrue(shell.Output);
    }

    [Fact]
    public async Task Write_ReachesTheShellAndOutputComesBack()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir);
        await shell.SendAsync("echo out-$((20+22))\r");

        (await shell.WaitForAsync("out-42")).ShouldBeTrue(shell.Output);
    }

    [Fact]
    public async Task Output_KeepsUtf8Intact()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir);
        await shell.SendAsync("printf 'caf\\xc3\\xa9 \\xe2\\x9c\\x93\\n'\r");

        (await shell.WaitForAsync("café ✓")).ShouldBeTrue(shell.Output);
    }

    [Fact]
    public async Task Resize_ChangesTheTerminalSize()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir, cols: 100, rows: 30);
        await shell.SendAsync("stty size\r");
        (await shell.WaitForAsync("30 100")).ShouldBeTrue(shell.Output);

        shell.Process.Resize(132, 40);
        await shell.SendAsync("stty size\r");

        (await shell.WaitForAsync("40 132")).ShouldBeTrue(shell.Output);
    }

    [Fact]
    public async Task Exit_ReportsTheExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir);
        await shell.SendAsync("exit 3\r");

        var exit = await shell.Process.Exited.WaitAsync(Timeout);
        exit.ShouldBe(new PtyExit(3, Killed: false));
    }

    [Fact]
    public async Task Kill_EndsTheShellAndTheProcessesItStarted()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir);
        await shell.SendAsync("sleep 1001 & echo BG=$!\r");
        (await shell.WaitForAsync(@"BG=\d+")).ShouldBeTrue(shell.Output);
        var background = int.Parse(Regex.Match(shell.Output, @"BG=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        await shell.SendAsync("sleep 1002\r");
        var foreground = await WaitForPidAsync("sleep 1002");

        shell.Process.Kill();

        var exit = await shell.Process.Exited.WaitAsync(Timeout);
        exit.ShouldBe(new PtyExit(null, Killed: true));
        (await WaitUntilGoneAsync(shell.Process.Pid)).ShouldBeTrue("shell still running");
        (await WaitUntilGoneAsync(foreground)).ShouldBeTrue("foreground child still running");
        (await WaitUntilGoneAsync(background)).ShouldBeTrue("background child still running");
    }

    [Fact]
    public async Task Environment_IsExactlyTheOneGiven()
    {
        if (!OperatingSystem.IsLinux()) return;

        const string fleetOnly = "FLEET_PTY_TEST_SECRET";
        Environment.SetEnvironmentVariable(fleetOnly, "leaked");
        try
        {
            var env = new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["HOME"] = _workDir,
                ["TERM"] = "xterm-256color",
                ["PS1"] = "$ ",
                ["FLEET_PTY_TEST_ADDED"] = "added",
            };
            await using var shell = await Shell.StartAsync(_workDir, env: env);
            await shell.SendAsync($"echo \"[${fleetOnly}][$FLEET_PTY_TEST_ADDED]\"\r");

            (await shell.WaitForAsync(@"\[\]\[added\]")).ShouldBeTrue(shell.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(fleetOnly, null);
        }
    }

    [Fact]
    public async Task ReadWriteAndResize_AfterExit_DoNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var shell = await Shell.StartAsync(_workDir, collect: false);
        await shell.SendAsync("exit\r");
        await shell.Process.Exited.WaitAsync(Timeout);

        var buffer = new byte[4096];
        var sw = Stopwatch.StartNew();
        while (await shell.Process.ReadAsync(buffer) > 0)
            sw.Elapsed.ShouldBeLessThan(Timeout);

        await shell.Process.WriteAsync("echo late\r"u8.ToArray());
        shell.Process.Resize(80, 24);
        shell.Process.Kill();
    }

    [Fact]
    public async Task DisposeAsync_IsSafeToCallTwice()
    {
        if (!OperatingSystem.IsLinux()) return;

        var shell = await Shell.StartAsync(_workDir);
        await shell.Process.DisposeAsync();
        await shell.Process.DisposeAsync();

        (await shell.Process.Exited.WaitAsync(Timeout)).Killed.ShouldBeTrue();
        await shell.DisposeAsync();
    }

    [Fact]
    public void EnvironmentChanges_RemovesWhatTheTargetLeavesOut()
    {
        Environment.SetEnvironmentVariable("FLEET_PTY_TEST_REMOVED", "x");
        try
        {
            var changes = PortaPtyFactory.EnvironmentChanges(new Dictionary<string, string> { ["ONLY"] = "1" });

            changes["FLEET_PTY_TEST_REMOVED"].ShouldBe(string.Empty);
            changes["ONLY"].ShouldBe("1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLEET_PTY_TEST_REMOVED", null);
        }
    }

    private static async Task<int> WaitForPidAsync(string commandLine)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                try
                {
                    var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    if (cmd == commandLine) return pid;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No process running '{commandLine}'");
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            if (!IsRunning(pid)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            // A zombie has ended; only its parent hasn't collected it yet.
            return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A bash with no rc files, so the prompt and timing don't depend on the machine.</summary>
    private sealed class Shell : IAsyncDisposable
    {
        private readonly StringBuilder _output = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _reader = Task.CompletedTask;

        private Shell(IPtyProcess process) => Process = process;

        public IPtyProcess Process { get; }

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        public static async Task<Shell> StartAsync(
            string workDir,
            int cols = 100,
            int rows = 30,
            IReadOnlyDictionary<string, string>? env = null,
            bool collect = true)
        {
            env ??= new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["HOME"] = workDir,
                ["TERM"] = "xterm-256color",
                ["PS1"] = "$ ",
            };
            var process = await new PortaPtyFactory().SpawnAsync(
                new PtySpawnOptions("/bin/bash", ["--norc", "--noprofile", "-i"], workDir, cols, rows, env));
            var shell = new Shell(process);
            if (collect) shell._reader = shell.CollectAsync();
            return shell;
        }

        public Task SendAsync(string text) => Process.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        public async Task<bool> WaitForAsync(string pattern)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < Timeout)
            {
                if (Regex.IsMatch(Output, pattern)) return true;
                await Task.Delay(25);
            }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Process.DisposeAsync();
            await _reader.WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _cts.Dispose();
        }

        private async Task CollectAsync()
        {
            // Decode per chunk: every chunk the tests look for is ASCII or arrives whole.
            var buffer = new byte[4096];
            int n;
            while ((n = await Process.ReadAsync(buffer, _cts.Token)) > 0)
            {
                lock (_output) _output.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
        }
    }
}
