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
