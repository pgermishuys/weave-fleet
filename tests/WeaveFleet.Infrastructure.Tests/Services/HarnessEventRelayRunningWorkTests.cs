using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Sessions;
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
    private readonly CapturingLogger _recorderLog = new();
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
                sp.GetRequiredService<IDelegationRepository>(), sp.GetRequiredService<IEventBroadcaster>(), sp.GetRequiredService<IUserContext>(),
                sessionActivityWriteService: null, activityTracker: null, sp.GetRequiredService<ISessionRepository>(), capabilitiesResolver: null));
            services.AddTransient<DomainEventTranslator>();
        });
        _recorder = new RunningWorkRecorder(scopeFactory, _recorderLog);
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
    public async Task An_end_that_names_only_the_child_ends_the_delegation_that_runs_in_it()
    {
        // A harness session that never saw the call (it was recreated) still knows which child session finished.
        var now = DateTime.UtcNow.ToString("O");
        _sessions.Seed(new Session { Id = "fleet-child", OpencodeSessionId = "ses_child", UserId = "user-1" });
        _work.Seed(new Delegation
        {
            Id = "w-sub", ParentSessionId = Session, WorkId = "call_sub", Kind = WorkKinds.Subagent, Title = "general",
            ChildSessionId = "fleet-child", Status = "running", CreatedAt = now, UpdatedAt = now,
        });
        var harness = new FakeHarnessSession("instance-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        _tracker.Register(harness.InstanceId, harness);
        harness.Emit(WorkEvents.Ended(new WorkReport { WorkId = "ses_child", ChildHarnessSessionId = "ses_child" }, WorkEndedReasons.Completed, "ses_1"));
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "ses_1", Timestamp = DateTimeOffset.UtcNow });
        await WaitUntilAsync(() => _publisher.Calls.Any(c => c.Event.Type == EventTypes.SessionIdle), cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        var work = _work.All.Single(w => w.Id == "w-sub");
        (work.Status, work.EndedReason).ShouldBe(("completed", WorkEndedReasons.Completed));

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

    [Fact]
    public async Task Work_left_running_after_its_end_was_missed_ends_when_the_session_goes_idle_with_the_reason_the_harness_reports()
    {
        var now = DateTime.UtcNow.ToString("O");
        _work.Seed(new Delegation { Id = "w-sub", ParentSessionId = Session, WorkId = "sub_1", Kind = WorkKinds.Subagent, Title = "reviewer", Status = "running", CreatedAt = now, UpdatedAt = now });
        var harness = new FakeHarnessSession("instance-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        _tracker.Register(harness.InstanceId, harness);
        await WaitUntilAsync(() => harness.RunningWorkCalls > 0, cts.Token);
        await _recorder.Idle(Session).WaitAsync(cts.Token);

        // The harness can say now; the end event never reached Fleet.
        harness.RunningWork = [new WorkReport { WorkId = "sub_1", EndedReason = WorkEndedReasons.Error, Detail = "the child failed" }];
        _work.All.Single().IsRunning.ShouldBeTrue();
        harness.Emit(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "ses_1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = System.Text.Json.JsonDocument.Parse("""{"status":{"type":"busy"}}""").RootElement.Clone(),
        });
        harness.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "ses_1", Timestamp = DateTimeOffset.UtcNow });

        await WaitUntilAsync(() => !_work.All.Single().IsRunning, cts.Token);
        _work.All.Single().EndedReason.ShouldBe(WorkEndedReasons.Error);

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_handler_that_never_finishes_logs_a_warning_and_lets_the_next_event_through()
    {
        _recorder.HandlerTimeout = TimeSpan.FromMilliseconds(200);
        // The harness never answers the catch-up when it attaches, so that handler hangs.
        var harness = new FakeHarnessSession("instance-1") { RunningWorkGate = new TaskCompletionSource().Task };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _relay.StartAsync(cts.Token);
        _tracker.Register(harness.InstanceId, harness);
        await WaitUntilAsync(() => harness.RunningWorkCalls > 0, cts.Token);

        harness.Emit(WorkEvents.Started(new WorkReport { WorkId = "sh_1", Kind = WorkKinds.Shell, Title = "shell", Label = "bun run dev", Background = true }, "ses_1"));

        await WaitUntilAsync(() => _work.All.Any(w => w.WorkId == "sh_1"), cts.Token);
        _recorderLog.Warnings.ShouldContain(m => m.Contains(Session) && m.Contains("reconcile"));

        harness.Complete();
        await _relay.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(20, ct);
    }

    private sealed class CapturingLogger : ILogger<RunningWorkRecorder>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings { get { lock (_warnings) return [.. _warnings]; } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                lock (_warnings)
                    _warnings.Add(formatter(state, exception));
        }
    }
}
