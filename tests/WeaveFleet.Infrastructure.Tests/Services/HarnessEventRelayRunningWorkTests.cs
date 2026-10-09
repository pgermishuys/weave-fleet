using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay hands a harness's running-work events to Fleet's record of it (<see cref="RunningWorkRecorder"/> and
/// <see cref="DelegationService"/>), which tells clients itself; the conversation never sees them. When a session's
/// harness attaches, work its harness no longer runs ends lost.
/// </summary>
public sealed class HarnessEventRelayRunningWorkTests : IDisposable
{
    private const string Session = "fleet-session";

    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryDelegationRepository _work = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly FakeEventPublisher _publisher = new();
    private readonly InstanceTracker _tracker = new();
    private readonly RunningWorkRecorder _recorder;
    private readonly HarnessEventRelay _relay;

    public HarnessEventRelayRunningWorkTests()
    {
        _sessions.Seed(new Session { Id = Session, InstanceId = "instance-1", UserId = "user-1", HarnessType = "opencode2" });
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(_sessions);
            services.AddSingleton<IDelegationRepository>(_work);
            services.AddSingleton<IEventBroadcaster>(_broadcaster);
            services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
            services.AddScoped(sp => new DelegationService(
                sp.GetRequiredService<IDelegationRepository>(), sp.GetRequiredService<IEventBroadcaster>(), sp.GetRequiredService<IUserContext>()));
            services.AddTransient<DomainEventTranslator>();
        });
        _recorder = new RunningWorkRecorder(scopeFactory, NullLogger<RunningWorkRecorder>.Instance);
        _relay = new HarnessEventRelay(
            _tracker, _broadcaster, _publisher, new SessionActivityTracker(), scopeFactory, NullLogger<HarnessEventRelay>.Instance,
            work: _recorder);
    }

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task Running_work_goes_to_fleets_record_and_not_to_the_conversation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        var harness = new FakeHarnessSession("instance-1");
        _tracker.Register(harness.InstanceId, harness);

        var shell = new WorkReport { WorkId = "sh_1", Kind = WorkKinds.Shell, Title = "shell", Label = "bun run dev", Background = true, CanStop = true };
        harness.Emit(WorkEvents.Started(shell, "ses_1"));
        harness.Emit(WorkEvents.Ended(new WorkReport { WorkId = "sh_1", Detail = "exit 0" }, WorkEndedReasons.Completed, "ses_1"));
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "ses_1", Timestamp = DateTimeOffset.UtcNow });

        await WaitUntilAsync(() => _publisher.Calls.Any(c => c.Event.Type == EventTypes.SessionIdle), cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        _publisher.Calls.ShouldNotContain(c => EventTypes.IsWorkEvent(c.Event.Type));
        var work = _work.All.ShouldHaveSingleItem();
        (work.ParentSessionId, work.Kind, work.Label, work.Status, work.EndedReason, work.Detail)
            .ShouldBe((Session, WorkKinds.Shell, "bun run dev", "completed", WorkEndedReasons.Completed, "exit 0"));
        _broadcaster.Broadcasts.Where(b => b.Topic == $"session:{Session}").Select(b => b.Type)
            .ShouldBe([EventTypes.WorkStarted, EventTypes.WorkEnded]);

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Work_the_harness_no_longer_runs_when_it_attaches_ends_lost()
    {
        var now = DateTime.UtcNow.ToString("O");
        _work.Seed(
            new Delegation { Id = "w-gone", ParentSessionId = Session, WorkId = "sh_gone", Kind = WorkKinds.Shell, Title = "shell", Status = "running", CreatedAt = now, UpdatedAt = now },
            new Delegation { Id = "w-still", ParentSessionId = Session, WorkId = "sh_still", Kind = WorkKinds.Shell, Title = "shell", Status = "running", CreatedAt = now, UpdatedAt = now });
        var harness = new FakeHarnessSession("instance-1") { RunningWork = [new WorkReport { WorkId = "sh_still" }] };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        _tracker.Register(harness.InstanceId, harness);

        await WaitUntilAsync(() => _work.All.Any(w => w.EndedReason is not null), cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        _work.All.Single(w => w.Id == "w-gone").EndedReason.ShouldBe(WorkEndedReasons.Lost);
        _work.All.Single(w => w.Id == "w-still").IsRunning.ShouldBeTrue();

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_harness_that_cant_say_what_runs_leaves_fleets_work_alone()
    {
        var now = DateTime.UtcNow.ToString("O");
        _work.Seed(new Delegation { Id = "w-1", ParentSessionId = Session, WorkId = "call_1", Title = "general", Status = "running", CreatedAt = now, UpdatedAt = now });
        var harness = new FakeHarnessSession("instance-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        _tracker.Register(harness.InstanceId, harness);
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "ses_1", Timestamp = DateTimeOffset.UtcNow });
        await WaitUntilAsync(() => _publisher.Calls.Any(c => c.Event.Type == EventTypes.SessionIdle), cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        _work.All.ShouldHaveSingleItem().IsRunning.ShouldBeTrue();

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }
}
