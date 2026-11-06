using System.Net;
using Shouldly;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>Add a machine Over SSH: hosts from ssh config, strict destinations, and the code fetched through ssh.</summary>
public sealed class SshMachineTests : IDisposable
{
    private const string Preview = """{"machineId":"machine-falcon","machineName":"falcon","os":"Linux","expiresAt":"2026-10-08T07:40:00Z"}""";
    private const string Redeemed = """{"deviceId":"d1","token":"fdt_d1.secret","machine":{"id":"machine-falcon","name":"falcon"}}""";
    private const string Identity = """{"id":"machine-falcon","name":"falcon","os":"Linux","apiVersion":1,"authMode":"token"}""";
    private const string PairJson = """{"code":"7K3M-Q9ZD","machineName":"falcon","addresses":["http://127.0.0.1:2113"]}""";

    private const string PairCommand = "env PATH=\"$HOME/.weave/fleet/bin:$PATH\" fleet node pair --json";

    private readonly FakeMachine _home = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeTunnels _tunnels = new();

    public void Dispose() => _home.Dispose();

    private SshMachineService Service => new(_home.Service, _tunnels, _runner, new SshInstallOptions { StartPause = TimeSpan.Zero, StartAttempts = 10 });

    [Theory]
    [InlineData("alice@falcon", "falcon", "alice", null, "alice@falcon")]
    [InlineData("falcon", "falcon", null, null, "falcon")]
    [InlineData("alice@falcon:2222", "falcon", "alice", 2222, "alice@falcon:2222")]
    [InlineData("alice@[fe80::1]:22", "fe80::1", "alice", 22, "alice@[fe80::1]:22")]
    [InlineData("alice@fe80::1", "fe80::1", "alice", null, "alice@[fe80::1]")]
    [InlineData("  falcon.example.com ", "falcon.example.com", null, null, "falcon.example.com")]
    public void A_destination_is_read_the_way_ssh_reads_it(string text, string host, string? user, int? port, string canonical)
    {
        var destination = SshDestination.Parse(text).ShouldNotBeNull();
        destination.Host.ShouldBe(host);
        destination.User.ShouldBe(user);
        destination.Port.ShouldBe(port);
        destination.Text.ShouldBe(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("alice@-oProxyCommand=evil")]
    [InlineData("falcon; rm -rf ~")]
    [InlineData("fal con")]
    [InlineData("falcon:99999")]
    [InlineData("falcon:abc")]
    [InlineData("a b@falcon")]
    [InlineData("$(id)@falcon")]
    public void Anything_that_could_be_an_ssh_option_or_a_command_is_refused(string text) =>
        SshDestination.Parse(text).ShouldBeNull();

    [Fact]
    public void Config_hosts_are_listed_without_wildcards()
    {
        var hosts = SshConfigHosts.Parse("""
            # work
            Host falcon
              HostName 100.64.90.72
              User alice
              Port 2222

            Host *.internal !secret box1 box2
              User shared

            Host *
              ServerAliveInterval 30

            Match host nope
              HostName ignored

            Host atlas
              HostName=atlas.example.com
            """);

        hosts.Select(h => h.Alias).ShouldBe(["box1", "box2", "falcon", "atlas"], ignoreOrder: true);
        var falcon = hosts.Single(h => h.Alias == "falcon");
        (falcon.HostName, falcon.User, falcon.Port).ShouldBe(("100.64.90.72", "alice", 2222));
        hosts.Single(h => h.Alias == "atlas").HostName.ShouldBe("atlas.example.com");
        hosts.Single(h => h.Alias == "box1").User.ShouldBe("shared");
    }

    private const string Probe = SshMachineService.ProbeCommand;

    private void HomeAcceptsTheCode() => _home.Answer = line => line switch
    {
        "POST /api/pairing/preview" => (HttpStatusCode.OK, Preview),
        "POST /api/pairing/redeem" => (HttpStatusCode.OK, Redeemed),
        "GET /api/machine" => (HttpStatusCode.OK, Identity),
        _ => null,
    };

    [Fact]
    public async Task A_machine_with_Fleet_logs_in_skips_the_install_and_pairs_through_a_tunnel()
    {
        _runner.Script = command => command == Probe ? new SshResult(0, "Darwin\n0.65.2\n", "") : new SshResult(0, PairJson + "\n", "");
        HomeAcceptsTheCode();
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("alice@orion", progress, CancellationToken.None);

        result.Error.ShouldBeNull();
        var machine = result.Machine.ShouldNotBeNull();
        machine.BaseUrl.ShouldBe("http://127.0.0.1:41999");
        machine.SshDestination.ShouldBe("alice@orion");
        _home.Service.TokenOf(machine).ShouldBe("fdt_d1.secret");
        _home.Requests.Single(r => r.Request == "POST /api/pairing/redeem").Body.ShouldNotBeNull().ShouldContain("7K3M-Q9ZD");
        _runner.Ran.Select(r => r.Command).ShouldBe([Probe, PairCommand]);
        progress.Steps.Select(step => (step.Key, step.State)).ShouldBe([
            ("login", "done"), ("install", "skipped"), ("start", "done"), ("pair", "done")]);
        progress.Steps[1].Detail.ShouldBe("Fleet 0.65.2 is already there");
        _tunnels.Closed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_machine_without_Fleet_gets_it_installed_started_and_paired_in_that_order()
    {
        var installed = false;
        var started = false;
        _runner.Script = command =>
        {
            if (command == Probe) return new SshResult(0, "Linux\nnone\n", "");
            if (command == SshMachineService.InstallCommand) { installed = true; return new SshResult(0, "", ""); }
            if (command == SshMachineService.StartCommand) { started = true; return new SshResult(0, "", ""); }
            return installed && started ? new SshResult(0, PairJson, "") : new SshResult(127, "", "sh: fleet: command not found");
        };
        HomeAcceptsTheCode();
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("alice@falcon", progress, CancellationToken.None);

        result.Machine.ShouldNotBeNull();
        _runner.Ran.Select(r => r.Command).ShouldBe([Probe, SshMachineService.InstallCommand, PairCommand, SshMachineService.StartCommand, PairCommand]);
        _runner.Timeouts[1].ShouldBe(TimeSpan.FromMinutes(3));
        progress.Steps.Select(step => step.State).ShouldBe(["done", "done", "done", "done"]);
        _tunnels.Opened.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Setting_up_waits_for_the_new_service_to_answer()
    {
        var pairs = 0;
        _runner.Script = command =>
        {
            if (command == Probe) return new SshResult(0, "Darwin\nnone\n", "");
            if (command is SshMachineService.InstallCommand or SshMachineService.StartCommand) return new SshResult(0, "", "");
            return ++pairs < 5 ? new SshResult(1, "", "No Fleet answered on port 2113.") : new SshResult(0, PairJson, "");
        };
        HomeAcceptsTheCode();

        var result = await Service.SetUpAsync("falcon", new SetupProgress(), CancellationToken.None);

        result.Machine.ShouldNotBeNull();
        pairs.ShouldBe(5);
    }

    [Fact]
    public async Task A_node_that_never_answers_names_the_start_step_and_tunnels_nothing()
    {
        _runner.Script = command => command switch
        {
            Probe => new SshResult(0, "Linux\nnone\n", ""),
            SshMachineService.InstallCommand or SshMachineService.StartCommand => new SshResult(0, "", ""),
            _ => new SshResult(1, "", "No Fleet answered on port 2113."),
        };
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("falcon", progress, CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("hasn't started answering");
        result.Diagnosis.ShouldNotBeNull().Code.ShouldBe(MachineDiagnoses.NotStarting);
        progress.Steps.Select(step => step.State).ShouldBe(["done", "done", "failed", "pending"]);
        _tunnels.Opened.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(255, "alice@falcon: Permission denied (publickey).", "never asks for a password", "ssh-key-rejected")]
    [InlineData(255, "Host key verification failed.", "Run ssh alice@falcon once and accept its key", "ssh-host-key")]
    [InlineData(255, "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\n@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\nHost key verification failed.", "ssh-keygen -R falcon", "ssh-host-key-changed")]
    [InlineData(255, "ssh: Could not resolve hostname falcon", "can't find falcon", "name-not-found")]
    [InlineData(255, "ssh: connect to host falcon port 22: Connection refused", "refuses SSH connections", "ssh-refused")]
    [InlineData(255, "ssh: connect to host falcon port 22: Connection timed out", "isn't answering on SSH", "ssh-timed-out")]
    [InlineData(255, "ssh: connect to host falcon port 22: No route to host", "couldn't connect", "ssh-unreachable")]
    [InlineData(-1, "No such file", "couldn't run ssh", "ssh-missing")]
    public async Task A_login_that_fails_names_the_login_step_and_runs_nothing_more(int exit, string stderr, string expected, string code)
    {
        _runner.Result = new SshResult(exit, "", stderr);
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("alice@falcon", progress, CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain(expected);
        result.Diagnosis.ShouldNotBeNull().Code.ShouldBe(code);
        progress.Steps[0].State.ShouldBe("failed");
        _runner.Ran.Count.ShouldBe(1);
        _tunnels.Opened.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(23, "curl: (23) Failure writing output\nsh: curl: command not found", "has no curl", "install-no-curl")]
    [InlineData(1, "install: no space left on device", "install on falcon stopped", "install-failed")]
    public async Task A_failed_install_is_said_plainly_and_nothing_is_started_or_paired(int exit, string stderr, string expected, string code)
    {
        _runner.Script = command => command switch
        {
            Probe => new SshResult(0, "Linux\nnone\n", ""),
            SshMachineService.InstallCommand => new SshResult(exit, "", stderr),
            _ => new SshResult(127, "", "command not found"),
        };
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("falcon", progress, CancellationToken.None);

        result.Error.ShouldNotBeNull().ShouldContain(expected);
        result.Diagnosis.ShouldNotBeNull().Code.ShouldBe(code);
        progress.Steps.Select(step => step.State).ShouldBe(["done", "failed", "pending", "pending"]);
        _runner.Ran.Select(r => r.Command).ShouldBe([Probe, SshMachineService.InstallCommand]);
    }

    [Fact]
    public async Task A_service_that_cannot_start_names_the_start_step()
    {
        _runner.Script = command => command switch
        {
            Probe => new SshResult(0, "Linux\n0.65.2\n", ""),
            SshMachineService.StartCommand => new SshResult(1, "", "Failed to connect to bus: No such file\nsystemctl --user failed"),
            _ => new SshResult(1, "", "No Fleet answered on port 2113."),
        };
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("falcon", progress, CancellationToken.None);

        result.Diagnosis.ShouldNotBeNull().Code.ShouldBe(MachineDiagnoses.ServiceFailed);
        progress.Steps.Select(step => step.State).ShouldBe(["done", "skipped", "failed", "pending"]);
    }

    [Fact]
    public async Task A_code_that_does_not_redeem_closes_the_tunnel_and_names_the_pair_step()
    {
        _runner.Script = command => command == Probe ? new SshResult(0, "Linux\n0.65.2\n", "") : new SshResult(0, PairJson, "");
        var progress = new SetupProgress();

        var result = await Service.SetUpAsync("falcon", progress, CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        progress.Steps[3].State.ShouldBe("failed");
        _tunnels.Closed.ShouldBe(_tunnels.Opened);
    }

    [Fact]
    public async Task A_tunnel_that_does_not_open_is_reported()
    {
        _runner.Script = command => command == Probe ? new SshResult(0, "Linux\n0.65.2\n", "") : new SshResult(0, PairJson, "");
        _tunnels.OpenError = "SSH logged in to falcon, but the tunnel to port 2113 didn't open.";

        var result = await Service.SetUpAsync("falcon", new SetupProgress(), CancellationToken.None);

        result.Error.ShouldBe(_tunnels.OpenError);
    }

    [Fact]
    public async Task A_bad_destination_runs_nothing()
    {
        var result = await Service.SetUpAsync("-oProxyCommand=x", new SetupProgress(), CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        _runner.Ran.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Linux\n0.65.2\n", "ready", "linux", "0.65.2")]
    [InlineData("Darwin\nnone\n", "noFleet", "macos", null)]
    public async Task A_probe_says_what_runs_there_and_changes_nothing(string output, string state, string os, string? version)
    {
        _runner.Result = new SshResult(0, output, "");

        var probe = await Service.ProbeAsync("falcon", CancellationToken.None);

        (probe.State, probe.Os, probe.Version).ShouldBe((state, os, version));
        _runner.Ran.ShouldBe([("falcon", Probe)]);
    }

    [Fact]
    public async Task A_probe_of_a_windows_machine_says_it_cannot_be_set_up_from_here()
    {
        _runner.Result = new SshResult(1, "", "'uname' is not recognized as an internal or external command");

        var probe = await Service.ProbeAsync("falcon", CancellationToken.None);

        probe.State.ShouldBe("unsupported");
        probe.Problem.ShouldNotBeNull().Diagnosis!.Code.ShouldBe(MachineDiagnoses.InstallUnsupported);
    }

    [Fact]
    public async Task A_probe_that_cannot_log_in_says_why()
    {
        _runner.Result = new SshResult(255, "", "alice@falcon: Permission denied (publickey).");

        var probe = await Service.ProbeAsync("alice@falcon", CancellationToken.None);

        probe.State.ShouldBe("cantLogIn");
        probe.Problem.ShouldNotBeNull().Diagnosis!.Code.ShouldBe(MachineDiagnoses.SshKeyRejected);
    }

    [Fact]
    public void Every_command_that_can_run_is_listed_and_the_install_uses_only_the_documented_source()
    {
        SshMachineService.Commands.ShouldBe([
            SshMachineService.ProbeCommand,
            "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh",
            "$HOME/.weave/fleet/bin/fleet node install-service",
            PairCommand,
        ]);
    }

    [Fact]
    public async Task Jobs_run_in_the_background_and_a_second_start_for_the_same_machine_joins_the_first()
    {
        var release = new TaskCompletionSource();
        _runner.Gate = release.Task;
        _runner.Script = command => command == Probe ? new SshResult(0, "Linux\n0.65.2\n", "") : new SshResult(0, PairJson, "");
        HomeAcceptsTheCode();
        var jobs = new SshSetupJobs(Service);

        var first = jobs.Start("falcon");
        var second = jobs.Start("falcon");

        second.ShouldBeSameAs(first);
        first.Status.ShouldBe("running");
        jobs.Get(first.Id).ShouldBeSameAs(first);
        release.SetResult();
        await Eventually(() => first.Status == "done");
        first.Result!.Machine.ShouldNotBeNull();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        condition().ShouldBeTrue();
    }

    private sealed class FakeRunner : ISshRunner
    {
        public SshResult Result { get; set; } = new(0, "", "");

        /// <summary>When set, answers each command; a null answer falls back to <see cref="Result"/>.</summary>
        public Func<string, SshResult?>? Script { get; set; }

        public List<(string Target, string Command)> Ran { get; } = [];
        public List<TimeSpan?> Timeouts { get; } = [];

        /// <summary>When set, commands wait for it before answering.</summary>
        public Task? Gate { get; set; }

        public async Task<SshResult> RunAsync(SshDestination destination, string command, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            Ran.Add((destination.Text, command));
            Timeouts.Add(timeout);
            if (Gate is not null)
                await Gate;
            return Script?.Invoke(command) ?? Result;
        }
    }

    private sealed class FakeTunnels : ISshTunnels
    {
        public string? OpenError { get; set; }
        public List<string> Opened { get; } = [];
        public List<string> Closed { get; } = [];

        public Task<(int Port, string? Error)> OpenAsync(string key, SshDestination destination, int? preferredPort, CancellationToken cancellationToken)
        {
            if (OpenError is not null)
                return Task.FromResult<(int, string?)>((0, OpenError));
            Opened.Add(key);
            return Task.FromResult<(int, string?)>((41999, null));
        }

        public void Close(string key) => Closed.Add(key);

        public bool IsUp(string key) => true;
    }
}
