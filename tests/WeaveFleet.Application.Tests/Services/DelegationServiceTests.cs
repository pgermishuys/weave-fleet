using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class DelegationServiceTests
{
    private readonly InMemoryDelegationRepository _delegationRepository = new();
    private readonly FakeEventBroadcaster _eventBroadcaster = new();
    private readonly IUserContext _userContext = new TestUserContext("user-1");
    private readonly DelegationService _sut;

    public DelegationServiceTests()
    {
        _sut = new DelegationService(_delegationRepository, _eventBroadcaster, _userContext);
    }

    [Fact]
    public async Task HandleDelegationDetectedAsync_WhenMissing_CreatesPendingDelegationAndBroadcasts()
    {
        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Code Review");

        result.ParentToolCallId.ShouldBe("tool-1");
        result.Title.ShouldBe("Code Review");
        result.Status.ShouldBe("pending");

        var inserted = _delegationRepository.All.Single();
        inserted.ParentSessionId.ShouldBe("parent-1");
        inserted.ParentToolCallId.ShouldBe("tool-1");
        inserted.Title.ShouldBe("Code Review");
        inserted.Status.ShouldBe("pending");
        inserted.ChildSessionId.ShouldBeNull();
        inserted.CompletedAt.ShouldBeNull();

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.created" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationDetectedAsync_WhenExisting_ReturnsExistingWithoutBroadcast()
    {
        var existing = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Code Review",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(existing);

        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Ignored");

        result.DelegationId.ShouldBe("del-1");
        result.Title.ShouldBe("Code Review");

        // No new insert (only the seeded one)
        _delegationRepository.All.Count.ShouldBe(1);
        _eventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleChildLinkedAsync_FromPending_UpdatesChildAndBroadcastsRunning()
    {
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Code Review",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(delegation);

        var result = await _sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");

        result.ShouldNotBeNull();
        result!.ChildSessionId.ShouldBe("child-1");
        result.Status.ShouldBe("running");

        var stored = _delegationRepository.All.Single();
        stored.ChildSessionId.ShouldBe("child-1");
        stored.Status.ShouldBe("running");

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.updated" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_FromRunning_TransitionsToTerminalAndBroadcasts()
    {
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            ChildSessionId = "child-1",
            Title = "Code Review",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(delegation);

        var result = await _sut.HandleDelegationFinishedAsync("del-1", "completed");

        result.ShouldNotBeNull();
        result!.Status.ShouldBe("completed");

        var stored = _delegationRepository.All.Single();
        stored.Status.ShouldBe("completed");

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.updated" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_WhenAlreadyTerminalWithDifferentStatus_Throws()
    {
        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            Title = "Code Review",
            Status = "completed",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        var act = () => _sut.HandleDelegationFinishedAsync("del-1", "error");

        await Should.ThrowAsync<InvalidOperationException>(act);
    }

    // ── Activity tracker integration ──────────────────────────────────────────

    [Fact]
    public async Task HandleChildLinkedAsync_WithActivityTracker_RegistersChildParentRelationship()
    {
        var activityTracker = new SessionActivityTracker();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: activityTracker);

        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Subagent",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        await sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");

        activityTracker.GetParentSessionId("child-1").ShouldBe("parent-1");
    }

    [Fact]
    public async Task HandleChildLinkedAsync_WithActivityTracker_ParentBusyWhenChildBusy()
    {
        var activityTracker = new SessionActivityTracker();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: activityTracker);

        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Subagent",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        activityTracker.Update("parent-1", "idle", "user-1");
        activityTracker.Update("child-1", "busy", "user-1");

        await sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");

        activityTracker.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_WithActivityTracker_UnregistersChildOnCompletion()
    {
        var activityTracker = new SessionActivityTracker();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: activityTracker);

        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            ChildSessionId = "child-1",
            Title = "Subagent",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        activityTracker.Update("parent-1", "idle", "user-1");
        activityTracker.Update("child-1", "busy", "user-1");
        activityTracker.RegisterChild("child-1", "parent-1");

        activityTracker.GetEffectiveActivityStatus("parent-1").ShouldBe("busy",
            "Parent should be busy before delegation finishes");

        await sut.HandleDelegationFinishedAsync("del-1", "completed");

        activityTracker.GetEffectiveActivityStatus("parent-1").ShouldBe("idle",
            "Parent should revert to idle after child delegation completes");
        activityTracker.GetParentSessionId("child-1").ShouldBeNull();
    }

    [Fact]
    public async Task HandleDelegationMovedToBackgroundAsync_FreesTheParent_AndSaysSoOnTheDelegation()
    {
        var activityTracker = new SessionActivityTracker();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: activityTracker);

        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            ChildSessionId = "child-1",
            Title = "Subagent",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
        activityTracker.RegisterChild("child-1", "parent-1");
        activityTracker.Update("parent-1", "idle", "user-1");
        activityTracker.Update("child-1", "busy", "user-1");

        var result = await sut.HandleDelegationMovedToBackgroundAsync("parent-1", "tool-1");

        // Still running: only the child's notice ends it.
        result!.Status.ShouldBe("running");
        _delegationRepository.All.Single().Status.ShouldBe("running");
        activityTracker.GetEffectiveActivityStatus("parent-1").ShouldBe("idle");

        var updated = _eventBroadcaster.Broadcasts.Single(b => b.Topic == "session:parent-1" && b.Type == "delegation.updated");
        updated.Payload.GetProperty("background").GetBoolean().ShouldBeTrue();
        updated.Payload.GetProperty("status").GetString().ShouldBe("running");

        // The list and the parent's conversation hear the parent is free.
        _eventBroadcaster.Broadcasts
            .Where(b => b.Type == "activity_status")
            .Select(b => (b.Topic, b.Payload.GetProperty("activityStatus").GetString()))
            .ShouldBe([("session:parent-1", "idle"), ("sessions", "idle")]);

        // Once is enough.
        _eventBroadcaster.Broadcasts.Clear();
        await sut.HandleDelegationMovedToBackgroundAsync("parent-1", "tool-1");
        _eventBroadcaster.Broadcasts.ShouldBeEmpty();

        // Its finish says background too, and after it the child is forgotten.
        await sut.HandleDelegationFinishedAsync("del-1", "completed");
        activityTracker.IsChildInBackground("child-1").ShouldBeFalse();
    }

    [Fact]
    public async Task HandleDelegationMovedToBackgroundAsync_LeavesAFinishedOrChildlessDelegationAlone()
    {
        var activityTracker = new SessionActivityTracker();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: activityTracker);

        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Subagent",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
        _delegationRepository.Seed(new Delegation
        {
            Id = "del-2",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-2",
            ChildSessionId = "child-2",
            Title = "Subagent",
            Status = "completed",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
        activityTracker.RegisterChild("child-2", "parent-1");

        (await sut.HandleDelegationMovedToBackgroundAsync("parent-1", "tool-1")).ShouldNotBeNull();
        (await sut.HandleDelegationMovedToBackgroundAsync("parent-1", "tool-2")).ShouldNotBeNull();
        (await sut.HandleDelegationMovedToBackgroundAsync("parent-1", "tool-3")).ShouldBeNull();

        activityTracker.IsChildInBackground("child-2").ShouldBeFalse();
        _eventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_step_of_a_delegation_reaches_progress_tracking()
    {
        var observer = new RecordingProgressObserver();
        var sut = new DelegationService(
            _delegationRepository, _eventBroadcaster, _userContext,
            sessionActivityWriteService: null, activityTracker: null, sessionRepository: null, capabilitiesResolver: null,
            progressObserver: observer);

        var created = await sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "shuttle", "Delete or update affected tests");
        await sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");
        await sut.HandleDelegationFinishedAsync(created.DelegationId, "completed");

        observer.Observed.Select(o => (o.SessionId, o.UserId, o.Event.GetType().Name)).ShouldBe(
        [
            ("parent-1", "user-1", nameof(WeaveFleet.Domain.Events.DelegationCreated)),
            ("parent-1", "user-1", nameof(WeaveFleet.Domain.Events.DelegationUpdated)),
            ("parent-1", "user-1", nameof(WeaveFleet.Domain.Events.DelegationCompleted)),
        ]);
        observer.Observed[0].Event.ShouldBeOfType<WeaveFleet.Domain.Events.DelegationCreated>().Payload.Description
            .ShouldBe("Delete or update affected tests");
        var linked = observer.Observed[1].Event.ShouldBeOfType<WeaveFleet.Domain.Events.DelegationUpdated>().Payload;
        (linked.ChildSessionId, linked.Title, linked.Status).ShouldBe(("child-1", "shuttle", "running"));
        observer.Observed[2].Event.ShouldBeOfType<WeaveFleet.Domain.Events.DelegationCompleted>().Payload.Status.ShouldBe("completed");
    }

    private sealed class RecordingProgressObserver : WeaveFleet.Application.Progress.ISessionProgressObserver
    {
        public List<(string SessionId, string? UserId, WeaveFleet.Domain.Events.DomainEvent Event)> Observed { get; } = [];

        public void Observe(string sessionId, string? userId, WeaveFleet.Domain.Events.DomainEvent? domainEvent)
        {
            if (domainEvent is not null)
                Observed.Add((sessionId, userId, domainEvent));
        }
    }
}
