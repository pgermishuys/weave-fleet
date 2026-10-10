using Shouldly;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>A supervisor over fakes, shut down and its folder deleted after each test.</summary>
public abstract class ModHostSupervisorTestBase : IAsyncLifetime
{
    protected const string User = ModHostRig.User;
    protected const string Chips = "test-chips";
    protected const string Demo = "demo-mod";
    protected const string S1 = "ses_test1";
    protected const string S2 = "ses_test2";

    internal ModHostRig Rig { get; } = new();

    internal ModHostSupervisor Supervisor => Rig.Supervisor;

    internal FakeModHostConnectionFactory Factory => Rig.Factory;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Rig.DisposeAsync();

    protected Task Ensure() => Supervisor.EnsureAsync().Within();

    protected Task<ModDispatchResult> Dispatch(string @event, string sessionId, string e = "{}", string? surface = null, CancellationToken ct = default)
        => Supervisor.DispatchAsync(new ModDispatchRequest(@event, sessionId, ModHostTests.Json(e), surface), ct);

    protected static string Kept(string name, int number) => ModIds.Kept(name, number);

    protected static string DraftId(string name, string sessionId) => ModIds.Draft(name, sessionId);

    /// <summary>Starts the host with what's seeded and checks it's running.</summary>
    protected async Task StartedAsync()
    {
        await Ensure();
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }

    /// <summary>The current host exits by itself; waits until Fleet has seen it.</summary>
    protected async Task CrashAsync(int code = 1)
    {
        var restarts = Supervisor.GetStatus().Restarts;
        Factory.Current.Exit(code);
        await ModHostTests.Eventually(() => Supervisor.GetStatus().Restarts == restarts + 1, "Fleet saw the host exit");
    }

    /// <summary>Moves fake time on by <paramref name="by"/>; waits until the host count is <paramref name="starts"/> and it's running.</summary>
    protected async Task RestartedAfterAsync(TimeSpan by, int starts)
    {
        Rig.Time.Advance(by);
        await ModHostTests.Eventually(() => Factory.Started.Count == starts && Supervisor.GetStatus().State == ModHostStates.Running, $"host {starts} is running");
    }
}
