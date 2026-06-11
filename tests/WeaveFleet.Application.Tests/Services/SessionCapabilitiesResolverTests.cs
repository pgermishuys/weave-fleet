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
    private const string Manual = "manual";
    private const string Automatic = "automatic";
    private const string Running = "running";
    private const string Stopped = "stopped";
    private const string Disconnected = "disconnected";
    private const string Completed = "completed";
    private const string Error = "error";
    private const string ArchivedReadOnlyReason = "Archived sessions are read-only.";
    private const string SessionNotRunningReason = "Session is not running.";
    private const string ResumeBeforePromptingReason = "Resume the session before prompting.";
    private const string AutomaticResumeOnPromptReason = "Automatic sessions resume on the next prompt.";
    private const string SessionNotResumableReason = "Session is not resumable.";
    private const string ArchivedCannotResumeReason = "Archived sessions cannot be resumed.";
    private const string SessionNotBusyReason = "Session is not busy.";
    private const string SessionAlreadyArchivedReason = "Session is already archived.";
    private const string SessionNotArchivedReason = "Session is not archived.";

    public static TheoryData<string, string, string, string, bool> StateCombinations => CreateStateCombinations();

    [Theory]
    [MemberData(nameof(StateCombinations))]
    public void resolve_returns_expected_capabilities_for_all_state_combinations(
        string runtimeMode,
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus,
        bool isLive)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve(
            runtimeMode,
            lifecycleStatus,
            retentionStatus,
            activityStatus,
            isLive);
        var effectiveLifecycleStatus = GetExpectedEffectiveLifecycleStatus(lifecycleStatus, isLive);
        var expected = CreateExpectedCapabilities(
            runtimeMode,
            effectiveLifecycleStatus,
            retentionStatus,
            activityStatus);

        capabilities.ShouldBe(expected);
    }

    [Theory]
    [InlineData(Active, false, true, true, false, true)]
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
            Manual,
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
    [InlineData(null, null, null, null, false, false, false, true)]
    [InlineData(" AUTOMATIC ", " STOPPED ", " ACTIVE ", " BUSY ", true, true, false, false)]
    [InlineData(" MANUAL ", " DISCONNECTED ", " ARCHIVED ", " BUSY ", false, false, true, false)]
    public void resolve_normalizes_missing_and_padded_state_values(
        string? runtimeMode,
        string? lifecycleStatus,
        string? retentionStatus,
        string? activityStatus,
        bool isLive,
        bool expectedCanPrompt,
        bool expectedCanUnarchive,
        bool expectedCanResume)
    {
        var capabilities = SessionCapabilitiesResolver.Resolve(
            runtimeMode,
            lifecycleStatus,
            retentionStatus,
            activityStatus,
            isLive);

        capabilities.CanPrompt.ShouldBe(expectedCanPrompt);
        capabilities.CanUnarchive.ShouldBe(expectedCanUnarchive);
        capabilities.CanResume.ShouldBe(expectedCanResume);
    }

    [Fact]
    public void disabled_reason_helpers_return_null_for_enabled_states()
    {
        InvokePrivateStatic<string?>("GetArchivedReadOnlyReason", [false]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetAlreadyArchivedReason", [false]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetPromptDisabledReason", [false, true, Stopped]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetStopDisabledReason", [false, Running]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetResumeDisabledReason", [false, false, Stopped]).ShouldBeNull();
        InvokePrivateStatic<string?>("GetAbortDisabledReason", [false, true, true]).ShouldBeNull();
    }

    [Fact]
    public void resolve_session_throws_when_session_is_null()
    {
        var sut = new SessionCapabilitiesResolver(new InstanceTracker());

        Should.Throw<ArgumentNullException>(() => sut.Resolve(null!))
            .ParamName.ShouldBe("session");
    }

    [Fact]
    public void resolve_session_uses_instance_tracker_to_determine_live_state()
    {
        var tracker = new InstanceTracker();
        var session = new Session
        {
            Id = "session-1",
            InstanceId = "instance-1",
            RuntimeMode = Manual,
            LifecycleStatus = Running,
            RetentionStatus = Active,
            ActivityStatus = Busy
        };
        var sut = new SessionCapabilitiesResolver(tracker);

        var withoutLiveInstance = sut.Resolve(session);
        tracker.Register("instance-1", new FakeHarnessSession("instance-1"));
        var withLiveInstance = sut.Resolve(session);

        withoutLiveInstance.CanPrompt.ShouldBeFalse();
        withoutLiveInstance.CanStop.ShouldBeFalse();
        withoutLiveInstance.CanResume.ShouldBeTrue();
        withoutLiveInstance.CanAbort.ShouldBeFalse();
        withLiveInstance.CanPrompt.ShouldBeTrue();
        withLiveInstance.CanStop.ShouldBeTrue();
        withLiveInstance.CanResume.ShouldBeFalse();
        withLiveInstance.CanAbort.ShouldBeTrue();
    }

    private static TheoryData<string, string, string, string, bool> CreateStateCombinations()
    {
        string[] runtimeModes = [Manual, Automatic];
        string[] lifecycleStatuses = [Running, Stopped, Completed, Disconnected, Error];
        string[] retentionStatuses = [Active, Archived];
        string[] activityStatuses = [Idle, Busy];
        bool[] liveStates = [true, false];
        var data = new TheoryData<string, string, string, string, bool>();

        foreach (var runtimeMode in runtimeModes)
        foreach (var lifecycleStatus in lifecycleStatuses)
        foreach (var retentionStatus in retentionStatuses)
        foreach (var activityStatus in activityStatuses)
        foreach (var isLive in liveStates)
        {
            data.Add(runtimeMode, lifecycleStatus, retentionStatus, activityStatus, isLive);
        }

        return data;
    }

    private static string GetExpectedEffectiveLifecycleStatus(string lifecycleStatus, bool isLive) =>
        string.Equals(lifecycleStatus, Running, StringComparison.Ordinal) && !isLive
            ? Disconnected
            : lifecycleStatus;

    private static SessionActionCapabilities CreateExpectedCapabilities(
        string runtimeMode,
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus)
    {
        var canPrompt = GetExpectedCanPrompt(runtimeMode, lifecycleStatus, retentionStatus);
        var canStop = GetExpectedCanStop(lifecycleStatus, retentionStatus);
        var canResume = GetExpectedCanResume(runtimeMode, lifecycleStatus, retentionStatus);
        var canRestart = !IsArchived(retentionStatus);
        var canAbort = GetExpectedCanAbort(lifecycleStatus, retentionStatus, activityStatus);
        var canArchive = !IsArchived(retentionStatus);
        var canUnarchive = IsArchived(retentionStatus);
        var canFork = !IsArchived(retentionStatus);

        return new SessionActionCapabilities(
            CanPrompt: canPrompt,
            CanStop: canStop,
            CanResume: canResume,
            CanRestart: canRestart,
            CanAbort: canAbort,
            CanArchive: canArchive,
            CanUnarchive: canUnarchive,
            CanFork: canFork,
            CanDelete: true,
            PromptDisabledReason: canPrompt ? null : GetExpectedPromptDisabledReason(retentionStatus),
            StopDisabledReason: canStop ? null : GetExpectedStopDisabledReason(retentionStatus),
            ResumeDisabledReason: canResume ? null : GetExpectedResumeDisabledReason(runtimeMode, lifecycleStatus, retentionStatus),
            RestartDisabledReason: canRestart ? null : ArchivedReadOnlyReason,
            AbortDisabledReason: canAbort ? null : GetExpectedAbortDisabledReason(lifecycleStatus, retentionStatus, activityStatus),
            ArchiveDisabledReason: canArchive ? null : SessionAlreadyArchivedReason,
            UnarchiveDisabledReason: canUnarchive ? null : SessionNotArchivedReason,
            ForkDisabledReason: canFork ? null : ArchivedReadOnlyReason,
            DeleteDisabledReason: null);
    }

    private static bool GetExpectedCanPrompt(string runtimeMode, string lifecycleStatus, string retentionStatus) =>
        !IsArchived(retentionStatus)
        && (string.Equals(lifecycleStatus, Running, StringComparison.Ordinal)
            || string.Equals(runtimeMode, Automatic, StringComparison.Ordinal)
            && lifecycleStatus is Stopped or Disconnected or Completed);

    private static bool GetExpectedCanStop(string lifecycleStatus, string retentionStatus) =>
        !IsArchived(retentionStatus) && string.Equals(lifecycleStatus, Running, StringComparison.Ordinal);

    private static bool GetExpectedCanResume(string runtimeMode, string lifecycleStatus, string retentionStatus) =>
        !IsArchived(retentionStatus)
        && string.Equals(runtimeMode, Manual, StringComparison.Ordinal)
        && lifecycleStatus is Stopped or Disconnected;

    private static bool GetExpectedCanAbort(string lifecycleStatus, string retentionStatus, string activityStatus) =>
        !IsArchived(retentionStatus)
        && string.Equals(lifecycleStatus, Running, StringComparison.Ordinal)
        && string.Equals(activityStatus, Busy, StringComparison.Ordinal);

    private static string GetExpectedPromptDisabledReason(string retentionStatus) =>
        IsArchived(retentionStatus) ? ArchivedReadOnlyReason : ResumeBeforePromptingReason;

    private static string GetExpectedStopDisabledReason(string retentionStatus) =>
        IsArchived(retentionStatus) ? ArchivedReadOnlyReason : SessionNotRunningReason;

    private static string GetExpectedResumeDisabledReason(
        string runtimeMode,
        string lifecycleStatus,
        string retentionStatus)
    {
        if (IsArchived(retentionStatus))
            return ArchivedCannotResumeReason;

        if (string.Equals(runtimeMode, Automatic, StringComparison.Ordinal))
            return AutomaticResumeOnPromptReason;

        return lifecycleStatus is Stopped or Disconnected
            ? throw new InvalidOperationException("Expected resumable manual lifecycle status to enable resume.")
            : SessionNotResumableReason;
    }

    private static string GetExpectedAbortDisabledReason(
        string lifecycleStatus,
        string retentionStatus,
        string activityStatus)
    {
        if (IsArchived(retentionStatus))
            return ArchivedReadOnlyReason;

        if (!string.Equals(lifecycleStatus, Running, StringComparison.Ordinal))
            return SessionNotRunningReason;

        return string.Equals(activityStatus, Busy, StringComparison.Ordinal)
            ? throw new InvalidOperationException("Expected busy running session to enable abort.")
            : SessionNotBusyReason;
    }

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
