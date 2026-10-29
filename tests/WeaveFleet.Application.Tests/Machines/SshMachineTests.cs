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

    [Fact]
    public async Task The_code_comes_through_ssh_and_is_redeemed_through_the_tunnel()
    {
        _runner.Result = new SshResult(0, PairJson + "\n", "");
        _home.Answer = line => line switch
        {
            "POST /api/pairing/preview" => (HttpStatusCode.OK, Preview),
            "POST /api/pairing/redeem" => (HttpStatusCode.OK, Redeemed),
            "GET /api/machine" => (HttpStatusCode.OK, Identity),
            _ => null,
        };

        var (machine, error) = await Service.AddAsync("alice@falcon", CancellationToken.None);

        error.ShouldBeNull();
        _runner.Ran.ShouldBe([("alice@falcon", PairCommand)]);
        machine!.BaseUrl.ShouldBe("http://127.0.0.1:41999");
        machine.SshDestination.ShouldBe("alice@falcon");
        _home.Service.TokenOf(machine).ShouldBe("fdt_d1.secret");
        _home.Requests.Single(r => r.Request == "POST /api/pairing/redeem").Body.ShouldNotBeNull().ShouldContain("7K3M-Q9ZD");
        _tunnels.Closed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_that_does_not_redeem_closes_the_tunnel_again()
    {
        _runner.Result = new SshResult(0, PairJson, "");

        var (machine, error) = await Service.AddAsync("falcon", CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNull();
        _tunnels.Opened.Count.ShouldBe(1);
        _tunnels.Closed.ShouldBe(_tunnels.Opened);
    }

    [Theory]
    [InlineData(127, "", "bash: fleet: command not found", "Fleet isn't installed there")]
    [InlineData(1, "", "No Fleet answered on port 2113. Start the node first", "Fleet isn't installed there")]
    [InlineData(255, "", "Host key verification failed.", "doesn't trust falcon")]
    [InlineData(255, "", "alice@falcon: Permission denied (publickey).", "never asks for a password")]
    [InlineData(255, "", "ssh: Could not resolve hostname falcon", "can't find falcon")]
    [InlineData(-1, "", "No such file", "couldn't run ssh")]
    public async Task What_went_wrong_is_said_plainly_and_nothing_is_tunnelled(int exit, string output, string stderr, string expected)
    {
        _runner.Result = new SshResult(exit, output, stderr);

        var (machine, error) = await Service.AddAsync("falcon", CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain(expected);
        _tunnels.Opened.ShouldBeEmpty();
        _home.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bad_destination_runs_nothing()
    {
        var (machine, error) = await Service.AddAsync("-oProxyCommand=x", CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNull();
        _runner.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tunnel_that_does_not_open_is_reported()
    {
        _runner.Result = new SshResult(0, PairJson, "");
        _tunnels.OpenError = "SSH logged in to falcon, but the tunnel to port 2113 didn't open.";

        var (_, error) = await Service.AddAsync("falcon", CancellationToken.None);

        error.ShouldBe(_tunnels.OpenError);
    }

    private static readonly string Install = string.Join(" && ", SshMachineService.InstallCommands);

    [Theory]
    [InlineData("Linux\n", "linux", true)]
    [InlineData("Darwin\n", "macos", true)]
    [InlineData("FreeBSD\n", "unsupported", false)]
    public async Task A_machine_without_Fleet_gets_the_commands_to_read_and_nothing_runs(string uname, string os, bool canInstall)
    {
        _runner.Script = command => command == "uname -s" ? new SshResult(0, uname, "") : new SshResult(127, "", "sh: fleet: command not found");

        var result = await Service.AddAsync("alice@falcon", CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("Fleet isn't installed there");
        var plan = result.Plan.ShouldNotBeNull();
        plan.Os.ShouldBe(os);
        plan.CanInstall.ShouldBe(canInstall);
        plan.Commands.ShouldBe(canInstall ? SshMachineService.InstallCommands : []);
        _runner.Ran.Select(r => r.Command).ShouldBe([PairCommand, "uname -s"]);
        _tunnels.Opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_windows_machine_is_not_installed_for_you()
    {
        _runner.Script = command => command == "uname -s"
            ? new SshResult(1, "", "'uname' is not recognized as an internal or external command")
            : new SshResult(1, "", "'env' is not recognized as an internal or external command");

        var result = await Service.AddAsync("falcon", CancellationToken.None);

        result.Plan.ShouldNotBeNull().CanInstall.ShouldBeFalse();
        result.Plan!.Commands.ShouldBeEmpty();
        result.Plan.Docs.ShouldStartWith("https://");
    }

    [Fact]
    public void The_install_commands_are_fixed_and_only_use_the_documented_install_source()
    {
        SshMachineService.InstallCommands.ShouldBe([
            "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh",
            "$HOME/.weave/fleet/bin/fleet node install-service",
        ]);
    }

    [Fact]
    public async Task Install_runs_the_commands_then_pairs_through_a_tunnel()
    {
        var installed = false;
        _runner.Script = command =>
        {
            if (command == "uname -s") return new SshResult(0, "Linux\n", "");
            if (command == Install) { installed = true; return new SshResult(0, "", ""); }
            return installed ? new SshResult(0, PairJson, "") : new SshResult(127, "", "sh: fleet: command not found");
        };
        _home.Answer = line => line switch
        {
            "POST /api/pairing/preview" => (HttpStatusCode.OK, Preview),
            "POST /api/pairing/redeem" => (HttpStatusCode.OK, Redeemed),
            "GET /api/machine" => (HttpStatusCode.OK, Identity),
            _ => null,
        };

        var result = await Service.InstallAndAddAsync("alice@falcon", CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Machine.ShouldNotBeNull().SshDestination.ShouldBe("alice@falcon");
        _runner.Ran.Select(r => r.Command).ShouldBe(["uname -s", Install, PairCommand]);
        _runner.Timeouts[1].ShouldBe(TimeSpan.FromMinutes(3));
        _tunnels.Opened.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Install_waits_for_the_new_service_to_answer()
    {
        var pairs = 0;
        _runner.Script = command =>
        {
            if (command == "uname -s") return new SshResult(0, "Darwin\n", "");
            if (command == Install) return new SshResult(0, "", "");
            return ++pairs < 4 ? new SshResult(1, "", "No Fleet answered on port 2113.") : new SshResult(0, PairJson, "");
        };
        _home.Answer = line => line switch
        {
            "POST /api/pairing/preview" => (HttpStatusCode.OK, Preview),
            "POST /api/pairing/redeem" => (HttpStatusCode.OK, Redeemed),
            "GET /api/machine" => (HttpStatusCode.OK, Identity),
            _ => null,
        };

        var result = await Service.InstallAndAddAsync("falcon", CancellationToken.None);

        result.Machine.ShouldNotBeNull();
        pairs.ShouldBe(4);
    }

    [Fact]
    public async Task Install_says_so_when_the_node_never_starts_and_tunnels_nothing()
    {
        _runner.Script = command => command switch
        {
            "uname -s" => new SshResult(0, "Linux\n", ""),
            _ when command == Install => new SshResult(0, "", ""),
            _ => new SshResult(1, "", "No Fleet answered on port 2113."),
        };

        var result = await Service.InstallAndAddAsync("falcon", CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("Fleet is installed on falcon, but it hasn't started answering");
        result.Plan.ShouldBeNull();
        _tunnels.Opened.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(23, "curl: (23) Failure writing output\nsh: curl: command not found", "has no curl")]
    [InlineData(1, "Failed to connect to bus: No such file\nsystemctl --user failed", "user service couldn't start")]
    [InlineData(1, "install: no space left on device", "install on falcon stopped")]
    [InlineData(255, "alice@falcon: Permission denied (publickey).", "never asks for a password")]
    public async Task A_failed_install_is_said_plainly_and_nothing_is_paired(int exit, string stderr, string expected)
    {
        _runner.Script = command => command switch
        {
            "uname -s" => new SshResult(0, "Linux\n", ""),
            _ when command == Install => new SshResult(exit, "", stderr),
            _ => new SshResult(127, "", "command not found"),
        };

        var result = await Service.InstallAndAddAsync("falcon", CancellationToken.None);

        result.Machine.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain(expected);
        _tunnels.Opened.ShouldBeEmpty();
        _runner.Ran.Select(r => r.Command).ShouldNotContain(PairCommand);
    }

    [Fact]
    public async Task Install_refuses_a_remote_windows_and_a_bad_destination_without_running_the_install()
    {
        _runner.Script = _ => new SshResult(1, "", "'uname' is not recognized");
        var windows = await Service.InstallAndAddAsync("falcon", CancellationToken.None);
        windows.Error.ShouldNotBeNull().ShouldContain("Linux and macOS only");

        var bad = await Service.InstallAndAddAsync("-oProxyCommand=x", CancellationToken.None);
        bad.Error.ShouldNotBeNull();

        _runner.Ran.Select(r => r.Command).ShouldNotContain(Install);
    }

    private sealed class FakeRunner : ISshRunner
    {
        public SshResult Result { get; set; } = new(0, "", "");

        /// <summary>When set, answers each command; a null answer falls back to <see cref="Result"/>.</summary>
        public Func<string, SshResult?>? Script { get; set; }

        public List<(string Target, string Command)> Ran { get; } = [];
        public List<TimeSpan?> Timeouts { get; } = [];

        public Task<SshResult> RunAsync(SshDestination destination, string command, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            Ran.Add((destination.Text, command));
            Timeouts.Add(timeout);
            return Task.FromResult(Script?.Invoke(command) ?? Result);
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
