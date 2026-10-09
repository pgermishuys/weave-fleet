using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionCapabilitiesResolverTests
{
    private const string Active = "active";
    private const string Archived = "archived";
    private const string Busy = "busy";
    private const string Idle = "idle";
    private const string Retry = "retry";
    private const string Running = "running";
    private const string Stopped = "stopped";
    private const string Disconnected = "disconnected";
    private const string Completed = "completed";
    private const string Error = "error";
    private const string ArchivedReadOnlyReason = "Archived sessions are read-only.";
    private const string SessionNotRunningReason = "Session is not running.";
    private const string SessionNotBusyReason = "Session is not busy.";
    private const string WaitForTurnReason = "Wait for the agent to finish its turn.";
    private const string SessionAlreadyArchivedReason = "Session is already archived.";
    private const string SessionNotArchivedReason = "Session is not archived.";

    public static TheoryData<string, string, string, bool> StateCombinations => CreateStateCombinations();

    [Theory]
    [MemberData(nameof(StateCombinations))]
    public void resolve_returns_expected_capabilities_for_all_state_combinations(
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus,
        bool isLive)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve(
            lifecycleStatus,
            retentionStatus,
            activityStatus,
            isLive);
        var effectiveLifecycleStatus = GetExpectedEffectiveLifecycleStatus(lifecycleStatus, isLive);
        var expected = CreateExpectedCapabilities(
            effectiveLifecycleStatus,
            retentionStatus,
            activityStatus);

        capabilities.ShouldBe(expected);
    }

    [Theory]
    [InlineData(Active, true, true, true, false, true)]
    [InlineData(Archived, false, false, false, true, false)]
    public void resolve_returns_expected_retention_capabilities(
        string retentionStatus,
        bool expectedCanPrompt,
        bool expectedCanArchive,
        bool expectedCanRestart,
        bool expectedCanUnarchive,
        bool expectedCanFork)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve(
            "stopped",
            retentionStatus,
            Idle,
            false);

        capabilities.CanPrompt.ShouldBe(expectedCanPrompt);
        capabilities.CanArchive.ShouldBe(expectedCanArchive);
        capabilities.CanRestart.ShouldBe(expectedCanRestart);
        capabilities.CanUnarchive.ShouldBe(expectedCanUnarchive);
        capabilities.CanFork.ShouldBe(expectedCanFork);
        capabilities.CanDelete.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, null, null, false, true, false)]
    [InlineData(" STOPPED ", " ACTIVE ", " BUSY ", true, true, false)]
    [InlineData(" DISCONNECTED ", " ARCHIVED ", " BUSY ", false, false, true)]
    public void resolve_normalizes_missing_and_padded_state_values(
        string? lifecycleStatus,
        string? retentionStatus,
        string? activityStatus,
        bool isLive,
        bool expectedCanPrompt,
        bool expectedCanUnarchive)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve(
            lifecycleStatus,
            retentionStatus,
            activityStatus,
            isLive);

        capabilities.CanPrompt.ShouldBe(expectedCanPrompt);
        capabilities.CanUnarchive.ShouldBe(expectedCanUnarchive);
    }

    [Fact]
    public void disabled_reason_helpers_return_null_for_enabled_states()
    {
        InvokePrivateStatic<string?>("GetArchivedReadOnlyReason", [false]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetAlreadyArchivedReason", [false]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetAbortDisabledReason", [false, true, true]).ShouldBeNull();
    }

    [Fact]
    public void resolve_session_throws_when_session_is_null()
    {
        var sut = new SessionCapabilitiesResolver(new InstanceTracker(), new SessionActivityTracker());

        Should.Throw<ArgumentNullException>(() => sut.Resolve(null!))
            .ParamName.ShouldBe("session");
    }

    [Fact]
    public void resolve_session_uses_instance_tracker_to_determine_live_state()
    {
        var tracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var session = new Session
        {
            Id = "session-1",
            InstanceId = "instance-1",
            LifecycleStatus = Running,
            RetentionStatus = Active,
            ActivityStatus = Busy
        };

        // Seed the activity tracker with the session's activity status
        activityTracker.Update("session-1", Busy, "test-user");

        var sut = new SessionCapabilitiesResolver(tracker, activityTracker);

        var withoutLiveInstance = sut.Resolve(session);
        tracker.Register("instance-1", new FakeHarnessSession("instance-1"));
        var withLiveInstance = sut.Resolve(session);

        withoutLiveInstance.CanPrompt.ShouldBeTrue();
        withoutLiveInstance.CanAbort.ShouldBeFalse();
        withoutLiveInstance.AbortDisabledReason.ShouldBe(SessionNotRunningReason);
        withLiveInstance.CanPrompt.ShouldBeTrue();
        withLiveInstance.CanAbort.ShouldBeTrue();
    }

    [Fact]
    public void fork_follows_the_harness_and_says_why_it_is_off()
    {
        var harnesses = new FakeHarnessRegistry();
        harnesses.Register(new FakeHarness("opencode2", "OpenCode 2", new HarnessCapabilities { SupportsForking = true }));
        harnesses.Register(new FakeHarness("claude-code", "Claude Code", new HarnessCapabilities { SupportsForking = false }));
        var sut = new SessionCapabilitiesResolver(new InstanceTracker(), new SessionActivityTracker(), harnesses);

        var canFork = sut.Resolve(new Session { Id = "a", InstanceId = "i-a", HarnessType = "opencode2" });
        var cantFork = sut.Resolve(new Session { Id = "b", InstanceId = "i-b", HarnessType = "claude-code" });
        var archived = sut.Resolve(new Session { Id = "c", InstanceId = "i-c", HarnessType = "claude-code", RetentionStatus = Archived });

        canFork.CanFork.ShouldBeTrue();
        canFork.ForkDisabledReason.ShouldBeNull();
        cantFork.CanFork.ShouldBeFalse();
        cantFork.ForkDisabledReason.ShouldBe("Claude Code can't copy a conversation, so its sessions can't be forked.");
        // Everything else about the session is as it was.
        cantFork.CanPrompt.ShouldBeTrue();
        cantFork.CanArchive.ShouldBeTrue();
        archived.CanFork.ShouldBeFalse();
        archived.ForkDisabledReason.ShouldBe(ArchivedReadOnlyReason);
    }

    [Fact]
    public void a_subagent_session_takes_no_prompt_when_its_harness_cannot_resume_children()
    {
        var harnesses = new FakeHarnessRegistry();
        harnesses.Register(new FakeHarness("claude-code", "Claude Code",
            new HarnessCapabilities { SupportsChildSessions = true, ChildSessionsResumable = false }));
        harnesses.Register(new FakeHarness("opencode2", "OpenCode 2",
            new HarnessCapabilities { SupportsChildSessions = true, ChildSessionsResumable = true }));
        var sut = new SessionCapabilitiesResolver(new InstanceTracker(), new SessionActivityTracker(), harnesses);

        var claudeChild = sut.Resolve(new Session { Id = "a", InstanceId = "i-a", HarnessType = "claude-code", ParentSessionId = "p" });
        var claudeParent = sut.Resolve(new Session { Id = "p", InstanceId = "i-p", HarnessType = "claude-code" });
        var openCodeChild = sut.Resolve(new Session { Id = "b", InstanceId = "i-b", HarnessType = "opencode2", ParentSessionId = "q" });
        var archivedChild = sut.Resolve(new Session
        {
            Id = "c", InstanceId = "i-c", HarnessType = "claude-code", ParentSessionId = "p", RetentionStatus = Archived,
        });

        claudeChild.CanPrompt.ShouldBeFalse();
        claudeChild.PromptDisabledReason.ShouldBe("Claude Code can't prompt a subagent on its own. Ask the session that started it.");
        // The rest of the session is as it was: it can still be archived, and it isn't busy.
        claudeChild.CanArchive.ShouldBeTrue();
        claudeParent.CanPrompt.ShouldBeTrue();
        claudeParent.PromptDisabledReason.ShouldBeNull();
        openCodeChild.CanPrompt.ShouldBeTrue();
        archivedChild.PromptDisabledReason.ShouldBe(ArchivedReadOnlyReason);
    }

    [Fact]
    public void an_unknown_harness_is_not_refused_here()
    {
        // Fork itself reports a harness it can't find; the menu isn't the place to guess.
        SessionCapabilitiesResolver.ForkUnsupportedReason(null).ShouldBeNull();
    }

    private static TheoryData<string, string, string, bool> CreateStateCombinations()
    {
        string[] lifecycleStatuses = [Running, Stopped, Completed, Disconnected, Error];
        string[] retentionStatuses = [Active, Archived];
        string[] activityStatuses = [Idle, Busy, Retry];
        bool[] liveStates = [true, false];
        var data = new TheoryData<string, string, string, bool>();

        foreach (var lifecycleStatus in lifecycleStatuses)
        foreach (var retentionStatus in retentionStatuses)
        foreach (var activityStatus in activityStatuses)
        foreach (var isLive in liveStates)
        {
            data.Add(lifecycleStatus, retentionStatus, activityStatus, isLive);
        }

        return data;
    }

    private static string GetExpectedEffectiveLifecycleStatus(string lifecycleStatus, bool isLive) =>
        string.Equals(lifecycleStatus, Running, StringComparison.Ordinal) && !isLive
            ? Disconnected
            : lifecycleStatus;

    [Fact]
    public void compact_is_off_for_a_harness_that_cant_compact_and_says_why()
    {
        var harness = new FakeHarness("pi-like", "Pi-like", new HarnessCapabilities { SupportsCompaction = false });

        var capabilities = SessionCapabilitiesResolver.Resolve(
            "running",
            Active,
            Idle,
            isLive: true,
            compactUnsupportedReason: SessionCapabilitiesResolver.CompactUnsupportedReason(harness));

        capabilities.CanCompact.ShouldBeFalse();
        capabilities.CompactDisabledReason.ShouldBe("Pi-like can't be asked to compact its context.");
        SessionCapabilitiesResolver.CompactUnsupportedReason(new FakeHarness("oc", "OpenCode", new HarnessCapabilities { SupportsCompaction = true })).ShouldBeNull();
    }

    [Theory]
    [InlineData("busy", false)]
    [InlineData("waiting_input", false)]
    [InlineData("idle", true)]
    public void compact_waits_for_the_turn_to_end(string activityStatus, bool expectedCanCompact)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve("running", Active, activityStatus, isLive: true);

        capabilities.CanCompact.ShouldBe(expectedCanCompact);
        capabilities.CompactDisabledReason.ShouldBe(expectedCanCompact ? null : WaitForTurnReason);
    }

    private static SessionActionCapabilities CreateExpectedCapabilities(
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus)
    {
        var canPrompt = GetExpectedCanPrompt(lifecycleStatus, retentionStatus);
        var canRestart = !IsArchived(retentionStatus);
        var canAbort = GetExpectedCanAbort(lifecycleStatus, retentionStatus, activityStatus);
        var canArchive = !IsArchived(retentionStatus);
        var canUnarchive = IsArchived(retentionStatus);
        var canFork = !IsArchived(retentionStatus);
        var canCompact = canPrompt && !IsWorking(activityStatus);

        return new SessionActionCapabilities(
            CanPrompt: canPrompt,
            CanRestart: canRestart,
            CanAbort: canAbort,
            CanArchive: canArchive,
            CanUnarchive: canUnarchive,
            CanFork: canFork,
            CanDelete: true,
            PromptDisabledReason: canPrompt ? null : GetExpectedPromptDisabledReason(retentionStatus),
            RestartDisabledReason: canRestart ? null : ArchivedReadOnlyReason,
            AbortDisabledReason: canAbort ? null : GetExpectedAbortDisabledReason(lifecycleStatus, retentionStatus, activityStatus),
            ArchiveDisabledReason: canArchive ? null : SessionAlreadyArchivedReason,
            UnarchiveDisabledReason: canUnarchive ? null : SessionNotArchivedReason,
            ForkDisabledReason: canFork ? null : ArchivedReadOnlyReason,
            DeleteDisabledReason: null)
        {
            CanCompact = canCompact,
            CompactDisabledReason = canCompact
                ? null
                : IsArchived(retentionStatus)
                    ? ArchivedReadOnlyReason
                    : IsWorking(activityStatus) ? WaitForTurnReason : SessionNotRunningReason,
        };
    }

    private static bool GetExpectedCanPrompt(string lifecycleStatus, string retentionStatus) =>
        !IsArchived(retentionStatus)
        && lifecycleStatus is Running or Stopped or Disconnected or Completed;

    private static bool GetExpectedCanAbort(string lifecycleStatus, string retentionStatus, string activityStatus) =>
        !IsArchived(retentionStatus)
        && string.Equals(lifecycleStatus, Running, StringComparison.Ordinal)
        && IsWorking(activityStatus);

    private static string GetExpectedPromptDisabledReason(string retentionStatus) =>
        IsArchived(retentionStatus) ? ArchivedReadOnlyReason : SessionNotRunningReason;

    private static string GetExpectedAbortDisabledReason(
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus)
    {
        if (IsArchived(retentionStatus))
            return ArchivedReadOnlyReason;

        if (!string.Equals(lifecycleStatus, Running, StringComparison.Ordinal))
            return SessionNotRunningReason;

        return IsWorking(activityStatus)
            ? throw new InvalidOperationException("Expected busy running session to enable abort.")
            : SessionNotBusyReason;
    }

    // A retrying session is still in its turn, so it can be stopped like a busy one.
    private static bool IsWorking(string activityStatus) => activityStatus is Busy or Retry;

    private static bool IsArchived(string retentionStatus) =>
        string.Equals(retentionStatus, Archived, StringComparison.Ordinal);

    private static T InvokePrivateStatic<T>(string methodName, object?[] arguments)
    {
        var method = typeof(SessionCapabilitiesResolver).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.ShouldNotBeNull();
        return (T)method.Invoke(null, arguments)!;
    }
}
