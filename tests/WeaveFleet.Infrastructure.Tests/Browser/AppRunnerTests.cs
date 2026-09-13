using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class AppRunnerTests
{
    private const string Owner = "owner-user";

    [Theory]
    [InlineData("http://localhost:5173/", "http://localhost:5173/")]
    [InlineData("http://0.0.0.0:8080", "http://localhost:8080/")]
    [InlineData("http://[::]:5000/", "http://localhost:5000/")]
    [InlineData("http://127.0.0.1:3000/app).", "http://127.0.0.1:3000/app")]
    [InlineData("https://shop.localhost:7001/", "https://shop.localhost:7001/")]
    public void Printed_addresses_on_this_machine_are_kept_in_a_form_a_browser_can_open(string printed, string expected)
        => AppRunner.NormalizePrintedUrl(printed).ShouldBe(expected);

    [Fact]
    public void Printed_addresses_elsewhere_are_dropped()
        => AppRunner.NormalizePrintedUrl("http://example.com:5341/ingest").ShouldBeNull();

    [Fact]
    public void Listen_rows_give_their_port_and_inode()
    {
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:1435 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 91234 1 0000000000000000 100 0 0 10 0
               1: 0100007F:9C40 0100007F:1435 01 00000000:00000000 00:00000000 00000000  1000        0 91300 1 0000000000000000 20 4 30 10 -1
               2: 00000000:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 555 1 0000000000000000 100 0 0 10 0
            """;

        LinuxListeningPorts.ParseListeners(table).ShouldBe([(5173, 91234L), (80, 555L)]);
    }

    [Theory]
    [InlineData("4242 (node) S 17 4242 17 0 -1", 17)]
    [InlineData("4243 (Web Content (x)) R 4242 4243 17 0 -1", 4242)]
    public void The_parent_pid_counts_fields_from_the_end_of_the_name(string stat, int parent)
        => LinuxListeningPorts.ParentPid(stat).ShouldBe(parent);

    [Theory]
    [InlineData("/usr/lib/dotnet/dotnet exec /usr/lib/dotnet/sdk/10.0.112/DotnetTools/dotnet-watch/10.0.112-servicing/tools/net10.0/any/dotnet-watch.dll ", true)]
    [InlineData("/usr/lib/dotnet/dotnet run --no-build ", false)]
    [InlineData("/work/shop/bin/Debug/net10.0/Shop ", false)]
    public void Dotnet_watchs_own_process_is_a_helper_and_the_app_is_not(string cmdline, bool helper)
        => LinuxListeningPorts.IsHelperCommandLine(cmdline).ShouldBe(helper);

    [Fact]
    public async Task A_dev_server_is_found_from_what_it_prints_and_keeps_its_id_and_port_across_a_restart()
    {
        if (!OperatingSystem.IsLinux() || !OnPath("python3"))
            return;

        var folder = Directory.CreateTempSubdirectory("fleet-app-runner-");
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "index.html"), "<html><body>hello</body></html>");
        using var runner = NewRunner();
        var changes = Record(runner);
        try
        {
            // The telemetry address it prints first isn't one it listens on, so it's skipped.
            var app = await StartAsync(runner, "app_1", "ses-1", folder.FullName,
                "echo 'exporting to http://localhost:5341/ingest'; echo \"Serving at http://127.0.0.1:$PORT/\"; exec python3 -m http.server $PORT --bind 127.0.0.1");

            var ready = await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(30));
            ready.Problem.ShouldBeNull();
            ready.Url.ShouldBe($"http://127.0.0.1:{app.Port}/");
            runner.Find(app.Id)!.Ports.ShouldBe([app.Port]);
            runner.Find(app.Id)!.Pid.ShouldNotBeNull();

            var restarted = (await runner.RestartAsync(app.Id)).App!;
            (restarted.Id, restarted.Port).ShouldBe((app.Id, app.Port));
            (await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(30))).Url.ShouldBe(ready.Url);

            (await runner.StopAsync(app.Id)).ShouldBeTrue();
            await Task.Delay(500);
            var stopped = runner.Find(app.Id)!;
            (stopped.Status, stopped.ExitCode, stopped.Pid).ShouldBe((AppRunStatus.Stopped, (int?)null, (int?)null));

            // A restart's kill isn't news, and neither is a stop's.
            ReasonsFor(changes, app.Id).ShouldBe([AppChangeReason.Started, AppChangeReason.Ready, AppChangeReason.Restarted, AppChangeReason.Ready, AppChangeReason.Stopped]);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_command_that_exits_reports_its_exit_code_and_output()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner();
        var changes = Record(runner);
        var app = await StartAsync(runner, "app_1", "ses-1", Path.GetTempPath(), "echo 'missing module'; exit 3");

        var ready = await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(10));

        ready.Url.ShouldBeNull();
        ready.Problem.ShouldBe("`echo 'missing module'; exit 3` exited with code 3 before serving a page.");
        runner.Logs(app.Id, 10).ShouldContain("missing module");
        await WaitForAsync(() => ReasonsFor(changes, app.Id).Contains(AppChangeReason.Exited));
        changes.Last(change => change.App.Id == app.Id).App.ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task A_command_that_ends_at_once_is_told_as_started_then_exited()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner(new BrowserOptions { MaxAppsPerSession = 100, MaxApps = 100 });
        var changes = Record(runner);
        for (var i = 0; i < 20; i++)
            await StartAsync(runner, $"app_{i}", "ses-1", Path.GetTempPath(), "true");

        for (var i = 0; i < 20; i++)
        {
            var appId = $"app_{i}";
            await WaitForAsync(() => ReasonsFor(changes, appId).Count == 2);
            ReasonsFor(changes, appId).ShouldBe([AppChangeReason.Started, AppChangeReason.Exited]);
        }
    }

    [Fact]
    public async Task Starting_past_a_cap_is_refused_with_the_apps_that_are_running()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner(new BrowserOptions { MaxAppsPerSession = 2, MaxApps = 3 });
        await StartAsync(runner, "app_1", "ses-1", Path.GetTempPath(), "exec sleep 30");
        await StartAsync(runner, "app_2", "ses-1", Path.GetTempPath(), "exec sleep 31");

        var third = await runner.StartAsync(new AppRunRequest("app_3", "ses-1", Owner, Path.GetTempPath(), "exec sleep 32"));

        third.App.ShouldBeNull();
        third.Refusal.ShouldBe("This session already runs 2 apps, its limit: `exec sleep 30` (app_1), `exec sleep 31` (app_2). Stop one from its Browser tab first, or restart one of these instead.");
        (await runner.RestartAsync("app_1")).App.ShouldNotBeNull();

        await StartAsync(runner, "app_4", "ses-2", Path.GetTempPath(), "exec sleep 33");
        (await runner.StartAsync(new AppRunRequest("app_5", "ses-3", Owner, Path.GetTempPath(), "exec sleep 34"))).Refusal!
            .ShouldStartWith("Fleet already runs 3 apps across sessions, its limit:");

        await runner.StopAsync("app_2");
        (await runner.StartAsync(new AppRunRequest("app_3", "ses-1", Owner, Path.GetTempPath(), "exec sleep 32"))).App.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_run_gets_its_port_but_none_of_Fleets_own_settings_and_is_first_to_go_when_memory_runs_out()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Environment.SetEnvironmentVariable("Fleet__BrowserTestSecret", "s3cret");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS_BROWSER_TEST", "http://localhost:2113");
        try
        {
            using var runner = NewRunner();
            var app = await StartAsync(runner, "app_1", "ses-1", Path.GetTempPath(),
                "echo \"secret=${Fleet__BrowserTestSecret:-none} aspnet=${ASPNETCORE_URLS_BROWSER_TEST:-none} port=$PORT oom=$(cat /proc/self/oom_score_adj)\"");

            await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(10));

            runner.Logs(app.Id, 10).ShouldContain($"secret=none aspnet=none port={app.Port} oom=1000");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Fleet__BrowserTestSecret", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS_BROWSER_TEST", null);
        }
    }

    [Fact]
    public async Task Output_is_numbered_so_a_client_can_ask_for_what_it_has_not_seen()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner();
        var app = await StartAsync(runner, "app_1", "ses-1", Path.GetTempPath(), "echo one; echo two; echo three");
        await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(10));
        await WaitForAsync(() => runner.Output(app.Id, 0).Next == 3);

        runner.Output(app.Id, 0).Lines.ShouldBe(["one", "two", "three"]);
        var rest = runner.Output(app.Id, 2);
        (rest.Lines.ShouldHaveSingleItem(), rest.Next).ShouldBe(("three", 3L));
        runner.Output(app.Id, 3).Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_a_sessions_apps_leaves_other_sessions_running()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner();
        await StartAsync(runner, "app_1", "ses-1", Path.GetTempPath(), "exec sleep 30");
        await StartAsync(runner, "app_2", "ses-2", Path.GetTempPath(), "exec sleep 30");

        await runner.StopSessionAppsAsync("ses-1");

        runner.Find("app_1")!.Status.ShouldBe(AppRunStatus.Stopped);
        runner.Find("app_2")!.IsLive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_leftover_process_is_killed_only_when_its_start_time_matches()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runner = NewRunner();
        using var leftover = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        var startedAt = new DateTimeOffset(leftover.StartTime.ToUniversalTime(), TimeSpan.Zero);

        runner.KillLeftover(leftover.Id, startedAt.AddMinutes(-5)).ShouldBeFalse();
        leftover.HasExited.ShouldBeFalse();

        runner.KillLeftover(leftover.Id, startedAt).ShouldBeTrue();
        leftover.WaitForExit(5000).ShouldBeTrue();
        runner.KillLeftover(leftover.Id, startedAt).ShouldBeFalse();
    }

    [Theory]
    [InlineData("dotnet watch", true)]
    [InlineData("dotnet run", true)]
    [InlineData("  dotnet watch run --no-hot-reload", true)]
    [InlineData("dotnet watch -- --urls http://localhost:$PORT", false)]
    [InlineData("npm run dev", false)]
    [InlineData("dotnet build", false)]
    public void Dotnet_run_and_watch_of_one_web_project_get_their_port_through_DOTNET_URLS(string command, bool applies)
    {
        var folder = Directory.CreateTempSubdirectory("fleet-dotnet-urls-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "Shop.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
            AppRunner.DotnetUrlsApplies(command, folder.FullName).ShouldBe(applies);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_Aspire_AppHost_or_a_folder_with_several_projects_gets_no_DOTNET_URLS()
    {
        var folder = Directory.CreateTempSubdirectory("fleet-dotnet-urls-");
        try
        {
            var host = Directory.CreateDirectory(Path.Combine(folder.FullName, "Shop.AppHost"));
            File.WriteAllText(Path.Combine(host.FullName, "Shop.AppHost.csproj"), """<Project Sdk="Aspire.AppHost.Sdk/9.4.0" />""");
            var web = Directory.CreateDirectory(Path.Combine(folder.FullName, "Shop.Web"));
            File.WriteAllText(Path.Combine(web.FullName, "Shop.Web.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
            File.WriteAllText(Path.Combine(folder.FullName, "One.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder.FullName, "Two.csproj"), "<Project />");

            AppRunner.DotnetUrlsApplies("dotnet run --project Shop.AppHost", folder.FullName).ShouldBeFalse();
            AppRunner.DotnetUrlsApplies("dotnet watch --project Shop.Web/Shop.Web.csproj", folder.FullName).ShouldBeTrue();
            AppRunner.DotnetUrlsApplies("dotnet watch", folder.FullName).ShouldBeFalse();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("System.IO.IOException: The configured user limit (128) on the number of inotify instances has been reached, or the per-process limit on the number of open file descriptors has been reached.", "max_user_instances")]
    [InlineData("Error: ENOSPC: System limit for number of file watchers reached, watch '/work/shop/src'", "max_user_watches")]
    public void Running_out_of_file_watchers_gets_a_line_saying_what_to_do(string line, string setting)
        => AppRunner.HintFor(line).ShouldNotBeNull().ShouldContain(setting);

    [Fact]
    public void Ordinary_output_gets_no_hint()
        => AppRunner.HintFor("  VITE v7.1.3  ready in 312 ms").ShouldBeNull();

    private static AppRunner NewRunner(BrowserOptions? browser = null)
        => new(new FleetOptions { Browser = browser ?? new BrowserOptions() }, NullLogger<AppRunner>.Instance);

    private static async Task<AppRunSnapshot> StartAsync(AppRunner runner, string id, string sessionId, string directory, string command)
    {
        var outcome = await runner.StartAsync(new AppRunRequest(id, sessionId, Owner, directory, command));
        outcome.Refusal.ShouldBeNull();
        return outcome.App!;
    }

    private static ConcurrentQueue<AppRunChange> Record(AppRunner runner)
    {
        var changes = new ConcurrentQueue<AppRunChange>();
        runner.Changed += changes.Enqueue;
        return changes;
    }

    private static List<AppChangeReason> ReasonsFor(IEnumerable<AppRunChange> changes, string appId)
        => [.. changes.Where(change => change.App.Id == appId).Select(change => change.Reason)];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().ShouldBeTrue();
    }

    private static bool OnPath(string command)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, command)));
}
