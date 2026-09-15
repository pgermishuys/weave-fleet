using WeaveFleet.Application.Services;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
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
            DeleteDisabledReason: null);
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
