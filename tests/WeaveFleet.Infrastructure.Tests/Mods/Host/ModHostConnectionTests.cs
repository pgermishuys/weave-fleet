using System.Diagnostics;
using System.Text.Json;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// The process and the connection, against shell scripts standing in for the host (so Unix only): <c>/bin/sh</c> is the
/// "Bun" and the script is the "host.js".
/// </summary>
public sealed class ModHostConnectionTests : IAsyncDisposable
{
    private const string InitializeOk = """echo '{"jsonrpc":"2.0","id":1,"result":{"protocol":1,"hostVersion":"1.2.3","bunVersion":"9.9.9"}}'""";
    private const string StayUp = "while read line; do :; done\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-modhost-").FullName;
    private readonly CapturingLogger _log = new();
    private IModHostConnection? _connection;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private string Script(string body)
    {
        var path = Path.Combine(_folder, "host.sh");
        File.WriteAllText(path, body);
        return path;
    }

    private string Healthy() => Script($"read line\n{InitializeOk}\n{StayUp}");

    private ModHostLaunch Launch(string script)
        => new("/bin/sh", script, Path.Combine(_folder, "work"), "0.40.0", "test-user");

    private Task<IModHostConnection> StartAsync(ModHostLaunch launch)
        => new ModHostConnectionFactory(_log).StartAsync(launch, new NoCalls(), CancellationToken.None);

    private static ModWireDispatch SessionStart(JsonElement e = default) => new("session.start", "ses_test1", e, [], null);

    private static bool IsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> BecomesGoneAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            if (IsGone(pid))
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task The_host_starts_with_an_empty_environment_in_the_launch_folder()
    {
        if (OperatingSystem.IsWindows())
            return;
        Environment.SetEnvironmentVariable("FLEET_SECRET_FOR_TEST", "hunter2");
        try
        {
            var envFile = Path.Combine(_folder, "env.txt");
            var pwdFile = Path.Combine(_folder, "pwd.txt");
            var script = Script($"/usr/bin/env > '{envFile}'\npwd > '{pwdFile}'\nread line\n{InitializeOk}\n{StayUp}");

            _connection = await StartAsync(Launch(script));

            File.ReadAllText(envFile).ShouldNotContain("FLEET_SECRET_FOR_TEST");
            File.ReadAllText(envFile).ShouldNotContain("HOME=");
            File.ReadAllText(pwdFile).Trim().ShouldEndWith("work");
            Directory.Exists(Path.Combine(_folder, "work")).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLEET_SECRET_FOR_TEST", null);
        }
    }

    [Fact]
    public async Task The_host_is_run_with_the_script_and_stdio_as_its_arguments()
    {
        if (OperatingSystem.IsWindows())
            return;
        var argsFile = Path.Combine(_folder, "args.txt");
        var script = Script($"echo \"$# $1\" > '{argsFile}'\nread line\n{InitializeOk}\n{StayUp}");

        _connection = await StartAsync(Launch(script));

        File.ReadAllText(argsFile).Trim().ShouldBe("1 --stdio");
    }

    [Fact]
    public async Task A_host_answering_protocol_one_is_ready_and_Host_has_its_versions()
    {
        if (OperatingSystem.IsWindows())
            return;

        _connection = await StartAsync(Launch(Healthy()));

        _connection.Host.ShouldBe(new ModHostInitializeResult(1, "1.2.3", "9.9.9"));
        _connection.ProcessId.ShouldBeGreaterThan(0);
        _connection.Exited.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task initialize_is_sent_with_protocol_one_and_fleets_version()
    {
        if (OperatingSystem.IsWindows())
            return;
        var seen = Path.Combine(_folder, "initialize.txt");
        var script = Script($"read line\necho \"$line\" > '{seen}'\n{InitializeOk}\n{StayUp}");

        _connection = await StartAsync(Launch(script));

        using var sent = JsonDocument.Parse(File.ReadAllText(seen));
        sent.RootElement.GetProperty("id").GetInt32().ShouldBe(1);
        sent.RootElement.GetProperty("method").GetString().ShouldBe("initialize");
        sent.RootElement.GetProperty("params").GetProperty("protocol").GetInt32().ShouldBe(1);
        sent.RootElement.GetProperty("params").GetProperty("fleetVersion").GetString().ShouldBe("0.40.0");
    }

    [Fact]
    public async Task A_host_answering_another_protocol_is_refused_and_killed()
    {
        if (OperatingSystem.IsWindows())
            return;
        var pidFile = Path.Combine(_folder, "pid.txt");
        var script = Script($"echo $$ > '{pidFile}'\nread line\necho '{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"protocol\":2,\"hostVersion\":\"2.0.0\",\"bunVersion\":\"9.9.9\"}}}}'\n{StayUp}");

        var thrown = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync(Launch(script)));

        thrown.Message.ShouldBe("The mod host speaks protocol 2; this Fleet needs protocol 1.");
        (await BecomesGoneAsync(int.Parse(File.ReadAllText(pidFile).Trim()))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_answering_the_protocol_error_is_refused_and_killed()
    {
        if (OperatingSystem.IsWindows())
            return;
        var pidFile = Path.Combine(_folder, "pid.txt");
        var script = Script($"echo $$ > '{pidFile}'\nread line\necho '{{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{{\"code\":-32000,\"message\":\"protocol 1 is not supported\"}}}}'\n{StayUp}");

        var thrown = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync(Launch(script)));

        thrown.Message.ShouldContain("protocol 1 is not supported");
        (await BecomesGoneAsync(int.Parse(File.ReadAllText(pidFile).Trim()))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_that_exits_at_once_is_refused_with_its_last_stderr_line()
    {
        if (OperatingSystem.IsWindows())
            return;
        var script = Script("echo 'first thing' >&2\necho 'bun: cannot open host.js' >&2\nexit 3\n");

        var thrown = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync(Launch(script)));

        thrown.Message.ShouldBe("The mod host stopped while starting: bun: cannot open host.js");
    }

    [Fact]
    public async Task A_missing_bun_or_script_is_refused_before_anything_starts()
    {
        var script = Script("exit 0\n");

        var noBun = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync(Launch(script) with { BunPath = Path.Combine(_folder, "no-bun") }));
        noBun.Message.ShouldContain("Bun");

        var noScript = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync(Launch(Path.Combine(_folder, "no-host.js"))));
        noScript.Message.ShouldContain("host.js");
        Directory.Exists(Path.Combine(_folder, "work")).ShouldBeFalse();
    }

    [Fact]
    public async Task Kill_stops_the_process_and_completes_Exited()
    {
        if (OperatingSystem.IsWindows())
            return;
        _connection = await StartAsync(Launch(Healthy()));

        _connection.Kill();
        _connection.Kill(); // again: never throws

        await _connection.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        (await BecomesGoneAsync(_connection.ProcessId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Requests_fail_with_Closed_once_the_process_is_killed()
    {
        if (OperatingSystem.IsWindows())
            return;
        _connection = await StartAsync(Launch(Healthy()));

        var pending = _connection.DispatchAsync(SessionStart(), TimeSpan.FromSeconds(30), CancellationToken.None);
        _connection.Kill();

        await Should.ThrowAsync<ModHostClosedException>(pending);
    }

    [Fact]
    public async Task A_dispatch_the_host_does_not_answer_times_out()
    {
        if (OperatingSystem.IsWindows())
            return;
        _connection = await StartAsync(Launch(Healthy()));

        await Should.ThrowAsync<TimeoutException>(
            _connection.DispatchAsync(SessionStart(), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task A_dispatch_is_sent_and_its_answer_read()
    {
        if (OperatingSystem.IsWindows())
            return;
        var script = Script($"read line\n{InitializeOk}\nread line\necho '{{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{{\"result\":null,\"failures\":[]}}}}'\n{StayUp}");
        _connection = await StartAsync(Launch(script));

        var result = await _connection.DispatchAsync(SessionStart(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Failures.ShouldBeEmpty();
        result.Result.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Shutdown_kills_a_host_that_ignores_it_within_the_grace()
    {
        if (OperatingSystem.IsWindows())
            return;
        _connection = await StartAsync(Launch(Healthy()));

        var clock = Stopwatch.StartNew();
        await _connection.ShutdownAsync(TimeSpan.FromMilliseconds(500));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1500));

        await _connection.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_lets_a_host_that_answers_and_exits_go_on_its_own()
    {
        if (OperatingSystem.IsWindows())
            return;
        var script = Script($"read line\n{InitializeOk}\nread line\necho '{{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{{}}}}'\nexit 0\n");
        _connection = await StartAsync(Launch(script));

        await _connection.ShutdownAsync(TimeSpan.FromSeconds(5));

        (await _connection.Exited).ShouldBe(0);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_leaves_no_process()
    {
        if (OperatingSystem.IsWindows())
            return;
        _connection = await StartAsync(Launch(Healthy()));
        var pid = _connection.ProcessId;

        await _connection.DisposeAsync();
        await _connection.DisposeAsync();

        (await BecomesGoneAsync(pid)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_hosts_stderr_lines_reach_the_log_with_the_user_key()
    {
        if (OperatingSystem.IsWindows())
            return;
        var script = Script($"echo 'hello from the host' >&2\nread line\n{InitializeOk}\n{StayUp}");
        _connection = await StartAsync(Launch(script));

        for (var i = 0; i < 50 && !_log.Messages.Contains("mods-host[test-user]: hello from the host"); i++)
            await Task.Delay(100);

        _log.Messages.ShouldContain("mods-host[test-user]: hello from the host");
    }

    private sealed class NoCalls : IModHostCalls
    {
        public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
            => throw new ModHostRpcException(ModHostErrorCodes.MethodNotFound, method);

        public void HandleNotification(string method, JsonElement parameters)
        {
        }
    }
}
