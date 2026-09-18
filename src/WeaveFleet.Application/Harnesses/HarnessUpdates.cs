using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Harnesses;

/// <summary>Where a harness's update stands. Sent to the client as <c>phase</c>.</summary>
public static class HarnessUpdatePhases
{
    /// <summary>Waiting for working sessions to finish before updating.</summary>
    public const string Waiting = "waiting";

    /// <summary>The updater is running.</summary>
    public const string Running = "running";

    /// <summary>The harness is on the new version.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The updater failed, timed out, or left the version unchanged.</summary>
    public const string Failed = "failed";
}

/// <summary>An update Fleet started for a harness, until the user dismisses its result.</summary>
/// <param name="Phase">One of <see cref="HarnessUpdatePhases"/>.</param>
/// <param name="Message">What happened, in a sentence; <see langword="null"/> while running.</param>
/// <param name="Output">The last lines the updater printed.</param>
/// <param name="WorkingSessions">While waiting: how many sessions are still working.</param>
/// <param name="FromVersion">The version before the update.</param>
/// <param name="ToVersion">The version after it, once it's done.</param>
public sealed record HarnessUpdateJob(
    string Phase,
    string? Message,
    string? Output,
    int WorkingSessions,
    string? FromVersion,
    string? ToVersion);

/// <summary>A harness's update state, shown in Settings → Harnesses.</summary>
public sealed record HarnessUpdateInfo
{
    /// <summary>The newest release, when update checks are on and the lookup worked.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>The installed version is older than <see cref="LatestVersion"/>, and Fleet can update it.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The oldest version Fleet works with, when the harness has one.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>The update command as the user would type it, to show or copy.</summary>
    public string? Command { get; init; }

    /// <summary>The update Fleet is running or just ran.</summary>
    public HarnessUpdateJob? Job { get; init; }
}

/// <summary>
/// Updates harnesses on the machine Fleet runs on: looks up their latest versions (cached for an hour) and runs a
/// harness's own updater when the user asks, once no session is working.
/// </summary>
public interface IHarnessUpdateService
{
    /// <summary>
    /// The update state for each harness in <paramref name="harnesses"/>. Looks the latest versions up again when
    /// <paramref name="checkLatest"/> and the cached answer is over an hour old, waiting a few seconds at most.
    /// </summary>
    Task<IReadOnlyDictionary<string, HarnessUpdateInfo>> DescribeAsync(
        IReadOnlyList<HarnessInfo> harnesses,
        bool checkLatest,
        CancellationToken ct);

    /// <summary>Starts updating the harness: it waits for working sessions, then runs the updater in the background.</summary>
    Task<Result<HarnessUpdateJob>> StartAsync(string harnessType, CancellationToken ct);

    /// <summary>Cancels an update still waiting for sessions, or dismisses a finished one. A running update can't be stopped.</summary>
    Result<Unit> Dismiss(string harnessType);
}
