using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Runtimes;

/// <summary>Where the Bun Fleet runs came from. Sent to the client as <c>source</c>.</summary>
public static class BunSources
{
    /// <summary>The path in <c>Fleet:Harness:BunPath</c>.</summary>
    public const string Configured = "configured";

    /// <summary>The Bun Fleet installed under <c>~/.weave/runtimes/bun/{version}/</c>.</summary>
    public const string Installed = "installed";

    /// <summary><c>bun</c> on <c>PATH</c>. Only looked for in Development.</summary>
    public const string Path = "path";
}

/// <summary>A Bun Fleet can run.</summary>
/// <param name="ExecutablePath">The absolute path of the <c>bun</c> executable.</param>
/// <param name="Source">One of <see cref="BunSources"/>.</param>
/// <param name="Version">The version, when Fleet installed it; <see langword="null"/> for a Bun found elsewhere.</param>
public sealed record BunLocation(string ExecutablePath, string Source, string? Version);

/// <summary>How a Bun Fleet found on the machine stands. Sent to the client as <c>status</c>.</summary>
public static class BunCandidateStatuses
{
    /// <summary>It runs and is <see cref="BunRelease.MinimumVersion"/> or later, so the user can pick it.</summary>
    public const string Usable = "usable";

    /// <summary>It runs, but it's older than <see cref="BunRelease.MinimumVersion"/>.</summary>
    public const string TooOld = "too-old";

    /// <summary>It didn't run, didn't answer in time, or didn't print a version.</summary>
    public const string NotWorking = "not-working";
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

/// <summary>Fleet installing the mod runtime, kept until the next install starts.</summary>
/// <param name="Phase">One of <see cref="BunInstallPhases"/>.</param>
/// <param name="Version">The Bun version being installed.</param>
/// <param name="Message">What's happening or what happened, in a sentence.</param>
/// <param name="BytesReceived">How much of the archive has downloaded.</param>
/// <param name="BytesTotal">The archive's size, when the server said.</param>
public sealed record BunInstallJob(
    string Phase,
    string Version,
    string? Message,
    long BytesReceived,
    long? BytesTotal);

/// <summary>
/// The Bun the mod host runs on. Fleet doesn't ship Bun: the first time the host needs it, Fleet downloads a pinned
/// version, checks its sha256 and installs it under <c>~/.weave/runtimes/bun/{version}/</c>.
/// </summary>
public interface IBunRuntime
{
    /// <summary>The Bun version Fleet installs.</summary>
    string Version { get; }

    /// <summary>The install running now or the last one, for the client to show; <see langword="null"/> before the first.</summary>
    BunInstallJob? Job { get; }

    /// <summary>
    /// The Bun to run, without downloading anything: the configured path, then the installed runtime, then <c>bun</c>
    /// on <c>PATH</c> in Development. <see langword="null"/> when there's none, or the configured path doesn't exist.
    /// </summary>
    BunLocation? Find();

    /// <summary>
    /// Finds Bun as <see cref="Find"/> does, or installs the pinned version when there's none. One install runs at a
    /// time; a caller arriving during one waits for it. A configured path that doesn't exist is an error, and nothing
    /// is downloaded. Cancelling stops the download and deletes what it wrote.
    /// </summary>
    /// <param name="progress">Told each change to <see cref="Job"/> while this call installs.</param>
    /// <param name="ct">Cancels the install.</param>
    Task<Result<BunLocation>> EnsureAsync(IProgress<BunInstallJob>? progress, CancellationToken ct);
}
