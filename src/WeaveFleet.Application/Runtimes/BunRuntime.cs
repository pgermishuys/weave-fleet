using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Runtimes;

/// <summary>Where the Bun Fleet runs came from. Sent to the client as <c>source</c>.</summary>
public static class BunSources
{
    /// <summary>The user's own Bun: the path in <c>Fleet:Harness:BunPath</c>, set by hand or picked from the Buns Fleet found.</summary>
    public const string Configured = "configured";

    /// <summary>The Bun Fleet installed under <c>~/.weave/runtimes/bun/{version}/</c>.</summary>
    public const string Installed = "installed";
}

/// <summary>A Bun Fleet can run.</summary>
/// <param name="ExecutablePath">The absolute path of the <c>bun</c> executable.</param>
/// <param name="Source">One of <see cref="BunSources"/>.</param>
/// <param name="Version">The version: the folder's name for Fleet's own Bun, what <c>--version</c> printed for the configured one.</param>
public sealed record BunLocation(string ExecutablePath, string Source, string Version);

/// <summary>How a Bun Fleet found on the machine stands. Sent to the client as <c>status</c>.</summary>
public static class BunCandidateStatuses
{
    /// <summary>It runs and is <see cref="BunRelease.MinimumVersion"/> or later, so the user can pick it.</summary>
    public const string Usable = "usable";

    /// <summary>It runs, but it's older than <see cref="BunRelease.MinimumVersion"/>.</summary>
    public const string TooOld = "too-old";

    /// <summary>It didn't run, didn't answer in time, or didn't print a version.</summary>
    public const string NotWorking = "not-working";

    /// <summary>Fleet didn't run it: it, or a folder above it, can be changed by other users (or Fleet couldn't tell), so it may not be the Bun it looks like.</summary>
    public const string NotChecked = "not-checked";
}

/// <summary>
/// A Bun Fleet found on the machine (on <c>PATH</c>, in <c>~/.bun/bin</c>, in Homebrew's folders) or the user typed.
/// Fleet only offers it: it's used once the user picks it, which saves <see cref="Path"/> as the configured path.
/// </summary>
/// <param name="Path">Where it was found, absolute. The path to save: it stays right after <c>bun upgrade</c> or
/// <c>brew upgrade</c>, where <paramref name="ResolvedPath"/> may not.</param>
/// <param name="ResolvedPath">The same file with every link followed; two candidates never share one.</param>
/// <param name="Version">What <c>bun --version</c> printed; <see langword="null"/> when it's not working.</param>
/// <param name="Status">One of <see cref="BunCandidateStatuses"/>.</param>
/// <param name="Message">Why it's too old or not working, in a sentence; <see langword="null"/> when usable.</param>
public sealed record BunCandidate(string Path, string ResolvedPath, string? Version, string Status, string? Message);

/// <summary>Whether a Bun is safe to keep running, judged against the release Fleet wants.</summary>
/// <param name="Safe">It's <see cref="BunRelease.OldestSafe"/> or later.</param>
/// <param name="UpdateAvailable">It's Fleet's own Bun and the release is newer, so Fleet will download the release.
/// Always <see langword="false"/> for the user's own Bun: Fleet never updates or replaces it.</param>
/// <param name="Message">What to tell the user when it isn't safe, in a sentence or two; <see langword="null"/> when it is.</param>
public sealed record BunSafety(bool Safe, bool UpdateAvailable, string? Message);

/// <summary>Where installing the mod runtime stands. Sent to the client as <c>phase</c>.</summary>
public static class BunInstallPhases
{
    /// <summary>The release archive is downloading.</summary>
    public const string Downloading = "downloading";

    /// <summary>The download is being checked against its pinned sha256.</summary>
    public const string Verifying = "verifying";

    /// <summary>The archive is being unpacked and moved into place.</summary>
    public const string Extracting = "extracting";

    /// <summary>Bun is installed.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The install failed or was cancelled; nothing it downloaded is left behind.</summary>
    public const string Failed = "failed";
}

/// <summary>Why an install failed. Sent to the client as <c>reason</c>; <see langword="null"/> unless the install failed.</summary>
public static class BunInstallFailures
{
    /// <summary>Fleet couldn't reach the download server: no DNS answer, a refused connection, or nothing arrived.</summary>
    public const string Offline = "offline";

    /// <summary>The server or something in between refused: a 403 or 407 answer, or a failed secure connection.</summary>
    public const string Blocked = "blocked";

    /// <summary>The download began and then stopped, or ended short.</summary>
    public const string Stopped = "stopped";

    /// <summary>The download didn't match the release's sha256 and was deleted.</summary>
    public const string Checksum = "checksum";

    /// <summary>The release has no Bun build for this computer.</summary>
    public const string NoBuild = "no-build";

    /// <summary>The user cancelled the install.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Anything else: another status, an archive that wouldn't unpack, a folder that wouldn't move.</summary>
    public const string Other = "other";
}

/// <summary>Fleet installing the mod runtime, kept until the next install starts.</summary>
/// <param name="Phase">One of <see cref="BunInstallPhases"/>.</param>
/// <param name="Version">The Bun version being installed.</param>
/// <param name="Message">What's happening or what happened, in a sentence.</param>
/// <param name="BytesReceived">How much of the archive has downloaded.</param>
/// <param name="BytesTotal">The archive's size, when the server said.</param>
/// <param name="Reason">One of <see cref="BunInstallFailures"/> when <paramref name="Phase"/> is <see cref="BunInstallPhases.Failed"/>; otherwise <see langword="null"/>.</param>
public sealed record BunInstallJob(
    string Phase,
    string Version,
    string? Message,
    long BytesReceived,
    long? BytesTotal,
    string? Reason = null);

/// <summary>
/// The Bun the mod host runs on: the user's own (the configured path, which they may have picked from the Buns Fleet
/// found on the machine), or the one Fleet installs under <c>~/.weave/runtimes/bun/{version}/</c> from the release
/// it wants (<see cref="IBunReleases"/>). Fleet never runs a Bun the user didn't choose, and never updates or replaces
/// the user's own.
/// </summary>
public interface IBunRuntime
{
    /// <summary>The install running now or the last one, for the client to show; <see langword="null"/> before the first.</summary>
    BunInstallJob? Job { get; }

    /// <summary>
    /// The machine's Bun, without downloading anything. With <c>Fleet:Harness:BunPath</c> configured: that Bun, when the path is absolute,
    /// exists, runs and is <see cref="BunRelease.MinimumVersion"/> or later; otherwise <see langword="null"/>.
    /// Without one: <paramref name="release"/>'s version when it's installed, otherwise the newest installed version,
    /// so mods keep running while a newer one downloads. Never looks on <c>PATH</c>.
    /// </summary>
    Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct);

    /// <summary>
    /// The Bun <paramref name="userId"/>'s mod host runs on, without downloading anything: <c>Fleet:Harness:BunPath</c>
    /// when configuration sets it, else the path the user saved, each checked as <see cref="FindAsync"/> checks a
    /// configured path (absolute, not changeable by others, runs, new enough); a path that fails the check finds
    /// nothing and doesn't fall back to Fleet's own. With no path at all: Fleet's own Bun, as <see cref="FindAsync"/>.
    /// </summary>
    Task<BunLocation?> FindForUserAsync(BunRelease release, string userId, CancellationToken ct);

    /// <summary>
    /// With a configured path: that Bun as <see cref="FindAsync"/> finds it, or an error saying why it can't run;
    /// nothing is downloaded. Without one: <paramref name="release"/>'s version, installing it when it isn't
    /// installed. One install runs at a time; a caller arriving during one waits for it. Cancelling stops the
    /// download and deletes what it wrote.
    /// </summary>
    /// <param name="release">The release to install.</param>
    /// <param name="progress">Told each change to <see cref="Job"/> while this call installs.</param>
    /// <param name="ct">Cancels the install.</param>
    Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct);

    /// <summary>
    /// Every Bun on the machine Fleet could offer the user: <c>bun</c> on <c>PATH</c>, then <c>~/.bun/bin</c>,
    /// <c>/opt/homebrew/bin</c> and <c>/usr/local/bin</c> (<c>bun.exe</c> on <c>PATH</c> and in
    /// <c>%USERPROFILE%\.bun\bin</c> on Windows), each once, with its version. Runs each one's
    /// <c>--version</c>. Finding one never makes Fleet use it.
    /// </summary>
    Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct);

    /// <summary>Checks a path the user typed the way <see cref="FindOnMachineAsync"/> checks what it finds. It must be absolute.</summary>
    Task<BunCandidate> CheckAsync(string path, CancellationToken ct);

    /// <summary>Fleet's installed Buns, newest first.</summary>
    IReadOnlyList<BunLocation> Installed();

    /// <summary>
    /// Deletes installed versions older than the newest, except any holding a path in <paramref name="inUse"/>
    /// (the Bun a running mod host started from). For the host supervisor to call once the host has moved to a newer
    /// version. Returns the versions it deleted; one it can't delete (a file in use) stays for a later prune.
    /// </summary>
    Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct);

    /// <summary>Whether <paramref name="location"/> is safe to keep running, judged against <paramref name="release"/>.</summary>
    BunSafety SafetyOf(BunLocation location, BunRelease release);
}
