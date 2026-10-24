using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Machines;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Machines;

/// <summary>
/// The SSH tunnels with a fake <c>ssh</c> script instead of the real one: what it is started with, that it comes back
/// after it drops, and that it is gone when the machine is removed or Fleet stops. (Linux and macOS: the fake is a
/// shell script.)
/// </summary>
#pragma warning disable CA1001 // Disposed in DisposeAsync.
public sealed class SshTunnelsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-ssh-{Guid.NewGuid():N}");
    private readonly InMemoryRemoteMachineRepository _repository = new();
    private SshTunnels? _tunnels;
    private RemoteMachineService? _machines;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_tunnels is not null)
            await _tunnels.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FakeSsh(string body)
    {
        var path = Path.Combine(_dir, "ssh");
        File.WriteAllText(path, $"#!/bin/sh\necho \"$$ $*\" >> \"{_dir}/calls\"\n{body}\n");
#pragma warning disable CA1416 // Only reached off Windows.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return path;
    }

    private SshTunnels Tunnels(string ssh, bool portAnswers = true, TimeSpan? readyTimeout = null)
    {
        var identity = new MachineIdentityStore(Path.Combine(_dir, "fleet.db"));
        _machines = new RemoteMachineService(_repository, new NoHttp(), new EphemeralDataProtectionProvider(), identity, new FakeTimeProvider());
        _tunnels = new SshTunnels(
            new SshOptions
            {
                Executable = ssh,
                InitialBackoff = TimeSpan.FromMilliseconds(30),
                MaxBackoff = TimeSpan.FromMilliseconds(120),
                StableAfter = TimeSpan.FromHours(1),
                ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(5),
            },
            _machines,
            NullLogger<SshTunnels>.Instance,
            ready: (_, _) => Task.FromResult(portAnswers));
        return _tunnels;
    }

    private string[] Calls() => File.Exists(Path.Combine(_dir, "calls")) ? File.ReadAllLines(Path.Combine(_dir, "calls")) : [];

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = Stopwatch.StartNew();
        while (!condition())
        {
            if (until.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Gave up waiting for {what}.");
            await Task.Delay(20);
        }
    }

    private static int Pid(string call) => int.Parse(call.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    private static bool Alive(int pid) => Directory.Exists($"/proc/{pid}") || (!OperatingSystem.IsLinux() && !Process.GetProcesses().All(p => p.Id != pid));

    [Fact]
    public async Task It_forwards_a_local_port_and_never_asks_or_trusts_on_its_own()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));

        var (port, error) = await tunnels.OpenAsync("m1", SshDestination.Parse("alice@falcon:2222")!, null, CancellationToken.None);

        error.ShouldBeNull();
        await Eventually(() => Calls().Length > 0, "ssh to start");
        var arguments = Calls()[0][(Calls()[0].IndexOf(' ') + 1)..];
        arguments.ShouldContain("-N");
        arguments.ShouldContain($"-L 127.0.0.1:{port}:127.0.0.1:2113");
        arguments.ShouldContain("BatchMode=yes");
        arguments.ShouldContain("ExitOnForwardFailure=yes");
        arguments.ShouldContain("-p 2222");
        arguments.ShouldEndWith("-- alice@falcon");
        arguments.ShouldNotContain("StrictHostKeyChecking");
        tunnels.IsUp("m1").ShouldBeTrue();
    }

    [Fact]
    public async Task It_starts_ssh_again_after_it_drops_and_the_machine_shows_tunnel_down_meanwhile()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        // The first two runs end at once; the third stays.
        var tunnels = Tunnels(FakeSsh($"""
            if [ "$(wc -l < "{_dir}/calls")" -lt 3 ]; then echo "connection reset" >&2; exit 255; fi
            exec sleep 60
            """));

        var (_, error) = await tunnels.OpenAsync("m1", SshDestination.Parse("falcon")!, null, CancellationToken.None);

        error.ShouldBeNull();
        await Eventually(() => Calls().Length >= 3 && tunnels.IsUp("m1"), "the third ssh to stay up");
        Calls().Length.ShouldBe(3);
    }

    [Fact]
    public async Task Closing_the_tunnel_kills_ssh_and_does_not_start_it_again()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));
        await tunnels.OpenAsync("m1", SshDestination.Parse("falcon")!, null, CancellationToken.None);
        await Eventually(() => Calls().Length == 1, "ssh to start");
        var pid = Pid(Calls()[0]);

        tunnels.Close("m1");

        await Eventually(() => !Alive(pid), "ssh to be killed");
        tunnels.IsUp("m1").ShouldBeFalse();
        await Task.Delay(300);
        Calls().Length.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_the_machine_stops_its_tunnel()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));
        await tunnels.StartAsync(CancellationToken.None);
        await _repository.UpsertAsync(Machine("m1", "http://127.0.0.1:1", "falcon"));
        _machines!.NotifyChanged("m1");
        await Eventually(() => tunnels.Keys().Contains("m1"), "the tunnel to be opened for the saved machine");
        await Eventually(() => Calls().Length == 1, "ssh to start");
        var pid = Pid(Calls()[0]);

        await _machines.DeleteAsync("m1");

        await Eventually(() => !tunnels.Keys().Contains("m1"), "the tunnel to be closed");
        await Eventually(() => !Alive(pid), "ssh to be killed");
    }

    [Fact]
    public async Task Stopping_Fleet_kills_every_tunnel()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));
        await tunnels.OpenAsync("m1", SshDestination.Parse("falcon")!, null, CancellationToken.None);
        await tunnels.OpenAsync("m2", SshDestination.Parse("atlas")!, null, CancellationToken.None);
        await Eventually(() => Calls().Length == 2, "both ssh to start");
        var pids = Calls().Select(c => Pid(c)).ToList();

        await tunnels.StopAsync(CancellationToken.None);

        foreach (var pid in pids)
            await Eventually(() => !Alive(pid), $"ssh {pid} to be killed");
    }

    [Fact]
    public async Task A_saved_ssh_machine_gets_its_tunnel_when_Fleet_starts_on_the_port_it_had()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));
        var free = System.Net.Sockets.TcpListener.Create(0);
        free.Start();
        var port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        await _repository.UpsertAsync(Machine("m1", $"http://127.0.0.1:{port}", "alice@falcon"));
        await _repository.UpsertAsync(Machine("m2", "https://atlas.test", null));

        await tunnels.StartAsync(CancellationToken.None);

        tunnels.Keys().ShouldBe(["m1"]);
        await Eventually(() => Calls().Length == 1, "ssh to start");
        Calls()[0].ShouldContain($"-L 127.0.0.1:{port}:");
        (await _repository.GetAsync("m1"))!.BaseUrl.ShouldBe($"http://127.0.0.1:{port}");
    }

    [Fact]
    public async Task A_port_that_is_taken_moves_the_machine_to_another()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("exec sleep 60"));
        using var taken = System.Net.Sockets.TcpListener.Create(0);
        taken.Start();
        var port = ((System.Net.IPEndPoint)taken.LocalEndpoint).Port;
        await _repository.UpsertAsync(Machine("m1", $"http://127.0.0.1:{port}", "falcon"));

        await tunnels.StartAsync(CancellationToken.None);

        (await _repository.GetAsync("m1"))!.BaseUrl.ShouldNotBe($"http://127.0.0.1:{port}");
    }

    [Fact]
    public async Task A_tunnel_that_never_opens_says_what_ssh_said()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var tunnels = Tunnels(FakeSsh("echo 'bind: Address already in use' >&2; exit 255"), portAnswers: false, readyTimeout: TimeSpan.FromMilliseconds(500));

        var (_, error) = await tunnels.OpenAsync("m1", SshDestination.Parse("falcon")!, null, CancellationToken.None);

        error.ShouldNotBeNull().ShouldContain("didn't open");
        error.ShouldContain("Address already in use");
        tunnels.Keys().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_runner_sends_the_command_without_prompts_and_returns_what_it_printed()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var ssh = FakeSsh("echo '{\"code\":\"7K3M-Q9ZD\"}'; echo oops >&2; exit 3");
        var runner = new SshProcessRunner(new SshOptions { Executable = ssh });

        var result = await runner.RunAsync(SshDestination.Parse("alice@falcon:2200")!, "fleet node pair --json", CancellationToken.None);

        result.ExitCode.ShouldBe(3);
        result.Output.ShouldContain("7K3M-Q9ZD");
        result.Error.ShouldContain("oops");
        var call = Calls().Single();
        call.ShouldContain("BatchMode=yes");
        call.ShouldContain("-p 2200");
        call.ShouldEndWith("-- alice@falcon fleet node pair --json");
    }

    [Fact]
    public async Task A_missing_ssh_is_reported_not_thrown()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake ssh is a shell script.

        var runner = new SshProcessRunner(new SshOptions { Executable = Path.Combine(_dir, "no-such-ssh") });

        var result = await runner.RunAsync(SshDestination.Parse("falcon")!, "x", CancellationToken.None);

        result.ExitCode.ShouldBe(-1);
    }

    private static RemoteMachine Machine(string id, string baseUrl, string? ssh) => new()
    {
        Id = id,
        Name = id,
        BaseUrl = baseUrl,
        EncryptedToken = "x",
        SshDestination = ssh,
    };

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
