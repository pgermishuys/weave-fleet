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

    private readonly FakeMachine _home = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeTunnels _tunnels = new();

    public void Dispose() => _home.Dispose();

    private SshMachineService Service => new(_home.Service, _tunnels, _runner);

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
        _runner.Ran.ShouldBe([("alice@falcon", "fleet node pair --json")]);
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

    private sealed class FakeRunner : ISshRunner
    {
        public SshResult Result { get; set; } = new(0, "", "");
        public List<(string Target, string Command)> Ran { get; } = [];

        public Task<SshResult> RunAsync(SshDestination destination, string command, CancellationToken cancellationToken)
        {
            Ran.Add((destination.Text, command));
            return Task.FromResult(Result);
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
