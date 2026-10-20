using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Updates;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Infrastructure.Tests.Updates;

/// <summary>When a Fleet may offer Restart: only when something will start it again.</summary>
public sealed class UpdateRestartCapabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-capability-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RestartCapability Capability(Dictionary<string, string> env, string? cgroup)
    {
        File.WriteAllText(Path.Combine(_root, "VERSION"), "0.59.0");
        var service = new UpdateRestartService(
            new FleetOptions(), new UpdateFiles(_root), null!, new UpdateRestartGate(), null!, new StubLifetime(),
            TimeProvider.System, NullLogger<UpdateRestartService>.Instance,
            environment: name => env.GetValueOrDefault(name), readCgroup: () => cgroup);
        return service.Capability();
    }

    [Fact]
    public void Inside_the_fleet_node_unit_it_can_restart()
        => Capability([], "0::/user.slice/user-1000.slice/user@1000.service/app.slice/fleet-node.service\n").CanRestart.ShouldBeTrue();

    [Fact]
    public void An_invocation_id_alone_does_not_count_because_a_terminal_unit_hands_it_to_its_shells()
    {
        var result = Capability(new() { ["INVOCATION_ID"] = "abc" }, "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-gnome-terminal.scope\n");

        result.CanRestart.ShouldBeFalse();
    }

    [Fact]
    public void The_launcher_variable_and_the_launchd_label_still_count()
    {
        Capability(new() { [UpdateRestartService.LauncherVariable] = "85" }, null).CanRestart.ShouldBeTrue();
        Capability(new() { ["XPC_SERVICE_NAME"] = "io.tryweave.fleet-node" }, null).CanRestart.ShouldBeTrue();
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
