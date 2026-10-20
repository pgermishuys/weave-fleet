using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Updates;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Updates;

/// <summary>The rollout against scripted machines, on a fake clock: nothing here waits for real time.</summary>
public sealed class UpdateRolloutServiceTests
{
    [Fact]
    public async Task Machines_update_one_at_a_time_and_a_busy_one_waits_for_its_sessions()
    {
        var rig = new Rig();
        rig.Node("falcon", working: [2, 1, 0]);
        rig.Node("heron");
        await rig.AddMachinesAsync();

        await rig.RunAsync();

        rig.Run.Status.ShouldBe("done");
        rig.Run.Steps.ShouldAllBe(step => step.State == "done");
        // falcon is first in the list: it is restarted only once its sessions have ended, and heron only after falcon is back.
        rig.Log.ShouldBe(["apply falcon", "falcon back on 0.59.1", "apply heron", "heron back on 0.59.1"]);
        rig.Nodes["falcon"].WorkingWhenApplied.ShouldBe(0);
        rig.Published.ShouldContain(step => step.Name == "falcon" && step.State == "waiting" && step.WaitingSessions == 2);
    }

    [Fact]
    public async Task A_machine_that_is_off_or_has_nothing_new_is_skipped_and_the_rest_carry_on()
    {
        var rig = new Rig();
        rig.Node("falcon").Reachable = false;
        rig.Node("heron");
        rig.Node("kite").Staged = null;
        await rig.AddMachinesAsync();

        await rig.RunAsync();

        rig.Run.Status.ShouldBe("done");
        rig.Run.Steps.Select(s => (s.Name, s.State, s.Detail)).ShouldBe(
        [
            ("falcon", "skipped", "can't be reached"),
            ("heron", "done", "back on 0.59.1"),
            ("kite", "skipped", "nothing downloaded yet"),
        ]);
    }

    [Fact]
    public async Task A_machine_that_cannot_restart_says_why_and_is_left_alone()
    {
        var rig = new Rig();
        rig.Node("falcon").CanRestart = false;
        await rig.AddMachinesAsync();

        await rig.RunAsync();

        rig.Run.Steps[0].Detail.ShouldBe("Restart Fleet once by hand.");
        rig.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_a_machine_goes_back_to_its_old_version_the_rollout_stops_before_the_next_one()
    {
        var rig = new Rig();
        rig.Node("falcon").AfterApply = Comeback.RollsBack;
        rig.Node("heron");
        await rig.AddMachinesAsync();

        await rig.RunAsync();

        rig.Run.Status.ShouldBe("stopped");
        rig.Run.Steps.Select(s => s.State).ShouldBe(["rolled-back", "pending"]);
        rig.Run.Summary!.ShouldContain("falcon didn't start on 0.59.1");
        rig.Log.ShouldBe(["apply falcon"]);
    }

    [Fact]
    public async Task A_machine_that_does_not_come_back_in_three_minutes_stops_the_rollout()
    {
        var rig = new Rig();
        rig.Node("falcon").AfterApply = Comeback.Never;
        rig.Node("heron");
        await rig.AddMachinesAsync();

        await rig.RunAsync();

        rig.Run.Status.ShouldBe("stopped");
        rig.Run.Steps.Select(s => s.State).ShouldBe(["no-return", "pending"]);
        rig.Time.GetUtcNow().ShouldBeGreaterThanOrEqualTo(rig.StartedAt + UpdateRolloutService.ComeBackLimit);
    }

    [Fact]
    public async Task Only_the_machines_asked_for_are_updated_and_a_second_rollout_waits_for_the_first()
    {
        var rig = new Rig();
        rig.Node("falcon", working: [1]);
        rig.Node("heron");
        await rig.AddMachinesAsync();
        var heron = rig.Ids["heron"];

        var first = await rig.Service.StartAsync([heron]);
        first.ShouldNotBeNull();
        (await rig.Service.StartAsync(null)).ShouldBeNull();
        await rig.PumpAsync();

        rig.Run.Steps.Select(s => s.Name).ShouldBe(["heron"]);
        rig.Log.ShouldBe(["apply heron", "heron back on 0.59.1"]);
        (await rig.Service.StartAsync(null)).ShouldNotBeNull();
        rig.Service.Cancel();
        await rig.PumpAsync();
        rig.Run.Status.ShouldBe("cancelled");
    }

    private enum Comeback { Updates, RollsBack, Never }

    private sealed class FakeNode(string name)
    {
        public bool Reachable = true;
        public bool CanRestart = true;
        public string Version = "0.59.0";
        public string? Staged = "0.59.1";
        public int[] Working = [0];
        public Comeback AfterApply = Comeback.Updates;
        public UpdateFailure? RolledBack;
        public int WorkingWhenApplied = -1;
        public int Reads;
        public int ReadsSinceApply = -1;
        public string Name { get; } = name;
    }

    private sealed class Rig : IUpdateNodes
    {
        private readonly FakeEventBroadcaster _broadcaster = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        public DateTimeOffset StartedAt { get; } = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        public Dictionary<string, FakeNode> Nodes { get; } = [];
        public Dictionary<string, string> Ids { get; } = [];
        public List<string> Log { get; } = [];
        private readonly InMemoryRemoteMachineRepository _repository = new();
        private readonly EphemeralDataProtectionProvider _protection = new();

        public UpdateRolloutService Service { get; }

        public Rig()
        {
            var machines = new RemoteMachineService(_repository, new NoHttp(), _protection, new MachineIdentityStore(Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db")), Time);
            Service = new UpdateRolloutService(machines, this, _broadcaster, Time, NullLogger<UpdateRolloutService>.Instance);
            _machines = machines;
        }

        private readonly RemoteMachineService _machines;

        public Task<bool> PushRuleAsync(RemoteMachine machine, string rule, CancellationToken ct) => Task.FromResult(true);

        public FakeNode Node(string name, int[]? working = null) => Nodes[name] = new FakeNode(name) { Working = working ?? [0] };

        public async Task AddMachinesAsync()
        {
            foreach (var node in Nodes.Values)
            {
                Ids[node.Name] = $"machine-{node.Name}";
                await _repository.UpsertAsync(new RemoteMachine
                {
                    Id = $"machine-{node.Name}",
                    Name = node.Name,
                    BaseUrl = $"https://{node.Name}.test",
                    EncryptedToken = _protection.CreateProtector("MachineTokens").Protect("token"),
                });
            }
        }

        public RolloutRun Run => Service.Current!;

        public IEnumerable<RolloutStep> Published => _broadcaster.Broadcasts.ToList()
            .Select(b => System.Text.Json.JsonSerializer.Deserialize<RolloutRun>(b.Payload, System.Text.Json.JsonSerializerOptions.Web)!)
            .SelectMany(run => run.Steps);

        public async Task RunAsync()
        {
            (await Service.StartAsync(null)).ShouldNotBeNull();
            await PumpAsync();
        }

        /// <summary>
        /// Moves the fake clock on while the rollout waits for it. Between waits the rollout talks to a machine, so the
        /// clock moves a little once it has (or, when it sits in one long wait, after the spins find nothing to wait for).
        /// </summary>
        public async Task PumpAsync()
        {
            while (!Service.Completion.IsCompleted)
            {
                var before = Volatile.Read(ref _calls);
                for (var spin = 0; spin < 2_000 && Volatile.Read(ref _calls) == before && !Service.Completion.IsCompleted; spin++)
                    await Task.Yield();
                Time.Advance(TimeSpan.FromSeconds(2));
            }

            await Service.Completion;
        }

        private int _calls;

        public Task<NodeUpdateInfo?> ReadAsync(RemoteMachine machine, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var node = Nodes[machine.Name];
            if (!node.Reachable)
                return Task.FromResult<NodeUpdateInfo?>(null);

            if (node.ReadsSinceApply >= 0)
            {
                // Restarting: it doesn't answer for a few reads, then it is back as the scenario says.
                if (++node.ReadsSinceApply < 3 || node.AfterApply == Comeback.Never)
                    return Task.FromResult<NodeUpdateInfo?>(null);
                if (node.AfterApply == Comeback.Updates)
                {
                    node.Version = node.Staged!;
                    node.Staged = null;
                    Log.Add($"{node.Name} back on {node.Version}");
                }
                else
                {
                    node.RolledBack = new UpdateFailure("0.59.0", node.Staged!, "it exited with code 1 before it was ready", "2026-10-20T09:01:00Z");
                    node.Staged = null;
                }

                node.ReadsSinceApply = -1;
            }

            var working = node.Working[Math.Min(node.Reads++, node.Working.Length - 1)];
            return Task.FromResult<NodeUpdateInfo?>(new NodeUpdateInfo(node.Version, node.Staged, working, node.CanRestart, node.CanRestart ? null : "Restart Fleet once by hand.", node.RolledBack));
        }

        public Task<NodeApplyResult> ApplyAsync(RemoteMachine machine, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var node = Nodes[machine.Name];
            node.WorkingWhenApplied = node.Working[Math.Min(Math.Max(node.Reads - 1, 0), node.Working.Length - 1)];
            node.ReadsSinceApply = 0;
            Log.Add($"apply {node.Name}");
            return Task.FromResult(new NodeApplyResult(NodeApplyOutcome.Accepted));
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The rollout talks through IUpdateNodes.");
    }
}
