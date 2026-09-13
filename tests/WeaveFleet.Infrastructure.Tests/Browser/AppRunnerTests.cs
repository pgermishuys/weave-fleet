using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class AppRunnerTests
{
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

    [Fact]
    public async Task A_dev_server_is_found_from_what_it_prints_and_keeps_its_port_across_a_restart()
    {
        if (!OperatingSystem.IsLinux() || !OnPath("python3"))
            return;

        var folder = Directory.CreateTempSubdirectory("fleet-app-runner-");
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "index.html"), "<html><body>hello</body></html>");
        using var runner = new AppRunner(NullLogger<AppRunner>.Instance);
        try
        {
            // The telemetry address it prints first isn't one it listens on, so it's skipped.
            var app = runner.Start("ses-1", folder.FullName,
                "echo 'exporting to http://localhost:5341/ingest'; echo \"Serving at http://127.0.0.1:$PORT/\"; exec python3 -m http.server $PORT --bind 127.0.0.1");

            var ready = await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(30));
            ready.Problem.ShouldBeNull();
            ready.Url.ShouldStartWith("http://127.0.0.1:");
            runner.Find(app.Id)!.Ports.ShouldHaveSingleItem().ShouldBe(new Uri(ready.Url!).Port);

            var restarted = await runner.RestartAsync(app.Id);
            restarted!.Id.ShouldBe(app.Id);
            (await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(30))).Url.ShouldBe(ready.Url);

            (await runner.StopAsync(app.Id)).ShouldBeTrue();
            await Task.Delay(500);
            runner.Find(app.Id)!.Status.ShouldBe(AppRunStatus.Exited);
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

        using var runner = new AppRunner(NullLogger<AppRunner>.Instance);
        var app = runner.Start("ses-1", Path.GetTempPath(), "echo 'missing module'; exit 3");

        var ready = await runner.WaitUntilReadyAsync(app.Id, TimeSpan.FromSeconds(10));

        ready.Url.ShouldBeNull();
        ready.Problem.ShouldBe("`echo 'missing module'; exit 3` exited with code 3 before serving a page.");
        runner.Logs(app.Id, 10).ShouldContain("missing module");
    }

    private static bool OnPath(string command)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, command)));
}
