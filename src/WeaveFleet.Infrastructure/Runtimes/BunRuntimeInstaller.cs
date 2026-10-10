using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Common;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.IO;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Finds the Bun the mod host runs on: the user's own (the configured path), or Fleet's, which it downloads from the
/// release it wants, checks against that release's sha256 and installs under <c>~/.weave/runtimes/bun/{version}/</c>.
/// Several versions can sit side by side, so mods keep running on the old one while a new one downloads. The archive
/// is unpacked next to its final place and moved in whole, so a half-finished install is never mistaken for a good one.
/// </summary>
internal sealed partial class BunRuntimeInstaller(
    FleetOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<BunRuntimeInstaller> logger) : IBunRuntime, IDisposable
{
    private const string ErrorCode = "Mods.Runtime";
    private const string Working = "Installing the mod runtime…";
    private const string ManifestFileName = "install.json";
    private const int MoveAttempts = 5;
    private const long ProgressStepBytes = 1024 * 1024;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (ProbeKey Key, BunVersion Version)> _probed = new();
    private BunInstallJob? _job;
    private BunMachineFinder? _finder;
    private Uri? _downloadBase;

    /// <summary>Test seam: the user's home folder.</summary>
    internal string Home { get; init; } = ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory;

    /// <summary>Test seam: the platform to install for.</summary>
    internal string Rid { get; init; } = BunRelease.CurrentRid();

    /// <summary>Test seam: where releases are downloaded from.</summary>
    internal Uri DownloadBase
    {
        get => _downloadBase ??= ResolveDownloadBase();
        init => _downloadBase = value;
    }

    /// <summary>Test seam: how long a download may go without a byte before it fails.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Test seam: moves the unpacked folder into place.</summary>
    internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;

    /// <summary>Test seam: renames a version folder out of the way before it's deleted.</summary>
    internal Action<string, string> RenameDirectory { get; init; } = Directory.Move;

    /// <summary>Test seam: the wait before the second try of the final move; each later wait doubles.</summary>
    internal TimeSpan MoveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Test seam: whether to hold a configured path to Windows' rules (it must end in <c>.exe</c>).</summary>
    internal bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Test seam: learns a Bun's version.</summary>
    internal Func<string, CancellationToken, Task<BunProbeResult>> Probe { get; init; } =
        (path, ct) => BunVersionProbe.RunAsync(path, BunVersionProbe.DefaultTimeout, ct);

    /// <summary>Test seam: finds the Buns on the machine.</summary>
    internal BunMachineFinder Finder
    {
        get => _finder ??= new BunMachineFinder { Home = Home, Probe = Probe };
        init => _finder = value;
    }

    private Uri ResolveDownloadBase()
    {
        var resolved = BunRelease.ResolveDownloadBase(options.Harness.BunDownloadBase, out var invalid);
        if (invalid)
            LogInvalidDownloadBase(options.Harness.BunDownloadBase);
        return resolved;
    }

    /// <inheritdoc />
    public BunInstallJob? Job => Volatile.Read(ref _job);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private string Root => Path.Combine(Home, ".weave", "runtimes", "bun");

    private string ExecutableName => Rid.StartsWith("win-", StringComparison.Ordinal) ? "bun.exe" : "bun";

    /// <inheritdoc />
    public Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct) => Finder.FindAsync(ct);

    /// <inheritdoc />
    public Task<BunCandidate> CheckAsync(string path, CancellationToken ct) => Finder.CheckAsync(path, ct);

    /// <inheritdoc />
    public async Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath))
            return (await CheckConfiguredAsync(options.Harness.BunPath, ct).ConfigureAwait(false)).Location;

        var installed = ScanInstalled(release);
        var wanted = installed.FirstOrDefault(bun => bun.Name == release.Version);
        if (wanted is not null)
            return wanted.Location;

        // Not there yet (it may be downloading): keep running the newest one that's there.
        var minimum = BunVersion.Parse(BunRelease.MinimumVersion);
        return installed.Where(bun => bun.Version >= minimum).OrderByDescending(bun => bun.Version).FirstOrDefault()?.Location;
    }

    /// <inheritdoc />
    public async Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath))
        {
            var (location, error) = await CheckConfiguredAsync(options.Harness.BunPath, ct).ConfigureAwait(false);
            return location is not null ? location : new FleetError(ErrorCode, error!);
        }

        if (FindRelease(release) is { } found)
            return found;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FindRelease(release) is { } installedMeanwhile)
                return installedMeanwhile;

            return await InstallAsync(release, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BunLocation> Installed() =>
        [.. ScanInstalled(null).OrderByDescending(bun => bun.Version).Select(bun => bun.Location)];

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(Root))
                return [];

            var installed = ScanInstalled(null);
            if (installed.Count == 0)
                return [];

            var newest = installed.Max(bun => bun.Version);
            // Links are followed on both sides, so a home reached through a symlink still matches the Bun in use.
            var protectedPaths = inUse.Select(NormalisePath).OfType<string>().ToList();
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var doomed = new List<(BunVersion Version, string Name, string Folder)>();
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(folder);
                if (!BunVersion.TryParse(name, out var version) || version >= newest)
                    continue;

                var prefix = BunPaths.Canonical(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (protectedPaths.Any(path => path.StartsWith(prefix, comparison)))
                    continue;

                doomed.Add((version, name, folder));
            }

            var deleted = new List<string>();
            foreach (var (_, name, folder) in doomed.OrderBy(entry => entry.Version))
            {
                // Renaming first is atomic, and fails while a running bun.exe holds the folder (Windows): then it waits for a later prune.
                var aside = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");
                try
                {
                    RenameDirectory(folder, aside);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogPruneSkipped(ex, name);
                    continue;
                }

                TryDelete(aside);
                LogPruned(name);
                deleted.Add(name);
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public BunSafety SafetyOf(BunLocation location, BunRelease release)
    {
        var oldestSafe = BunVersion.TryParse(release.OldestSafe, out var parsedOldest)
            ? parsedOldest
            : BunVersion.Parse(BunRelease.MinimumVersion);
        var installed = location.Source == BunSources.Installed;
        var known = BunVersion.TryParse(location.Version, out var version);
        var safe = known && version >= oldestSafe;
        var updateAvailable = installed && known && BunVersion.TryParse(release.Version, out var wanted) && version < wanted;
        if (safe)
            return new BunSafety(true, updateAvailable, null);

        var note = string.IsNullOrWhiteSpace(release.Note) ? "" : release.Note + " ";
        var message = installed
            ? $"Fleet's Bun {location.Version} needs a security fix: the oldest safe version is {release.OldestSafe}. {note}Fleet installs Bun {release.Version} to replace it."
            : $"Your Bun {location.Version} needs a security fix: the oldest safe version is {release.OldestSafe}. {note}Run bun upgrade and Fleet picks up the new version by itself, or use Fleet's own Bun instead.";
        return new BunSafety(false, updateAvailable, message);
    }

    /// <summary>The configured Bun when it can be used, otherwise why not.</summary>
    private async Task<(BunLocation? Location, string? Error)> CheckConfiguredAsync(string configured, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(configured))
            return (null, $"Fleet:Harness:BunPath must be an absolute path; it's {configured}.");

        if (IsWindows && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (null, $"Fleet:Harness:BunPath is {configured}, which must end in .exe: point it at bun.exe.");

        if (Directory.Exists(configured))
            return (null, $"Fleet:Harness:BunPath is {configured}, which is a folder, not the bun program.");

        var key = ProbeKeyOf(configured);
        if (key is null)
            return (null, $"Fleet:Harness:BunPath is {configured}, which doesn't exist.");

        BunVersion version;
        if (_probed.TryGetValue(configured, out var cached) && cached.Key == key)
        {
            version = cached.Version;
        }
        else
        {
            var probe = await Probe(configured, ct).ConfigureAwait(false);
            if (probe.Version is not { } probed)
                return (null, $"Fleet:Harness:BunPath is {configured}, which didn't run: {probe.Error}");

            // Only a Bun that ran is remembered, so a slow start or a Bun still being written is looked at again.
            _probed[configured] = (key, probed);
            version = probed;
        }

        if (version < BunVersion.Parse(BunRelease.MinimumVersion))
        {
            return (null, $"Bun {version} at {configured} is older than {BunRelease.MinimumVersion}, " +
                "the oldest Bun mods run on. Run bun upgrade, or use Fleet's own Bun.");
        }

        return (new BunLocation(configured, BunSources.Configured, version.ToString()), null);
    }

    /// <summary>What identifies the file as it is now, so a replaced one is probed again; <see langword="null"/> when there's no such file.</summary>
    private static ProbeKey? ProbeKeyOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var file = target as FileInfo ?? info;
            if (!file.Exists)
                return null;

            // The device and inode tell a replacement of the same size and time from the file probed before.
            ulong? device = null, inode = null;
            if (NativeFileStatus.TryStat(file.FullName, out var status))
                (device, inode) = (status.Dev, status.Ino);
            return new ProbeKey(Path.GetFullPath(path), target?.FullName, file.Length, file.LastWriteTimeUtc, device, inode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalisePath(string path)
    {
        try
        {
            return BunPaths.Canonical(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The release's own version, when it's installed and is the release's build.</summary>
    private BunLocation? FindRelease(BunRelease release) =>
        ScanInstalled(release).FirstOrDefault(bun => bun.Name == release.Version)?.Location;

    /// <summary>
    /// Every installed version folder. With a <paramref name="release"/>, the folder of its version also has to hold
    /// that release's build (same sha256), or it isn't counted.
    /// </summary>
    private List<InstalledBun> ScanInstalled(BunRelease? release)
    {
        var found = new List<InstalledBun>();
        if (!Directory.Exists(Root))
            return found;

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(folder);
            if (!BunVersion.TryParse(name, out var version))
                continue;

            string? sha = null;
            if (release is not null && name == release.Version)
            {
                if (release.AssetFor(Rid) is not { } asset)
                    continue;
                sha = asset.Sha256;
            }

            if (IsInstalled(folder, name, sha))
                found.Add(new InstalledBun(name, version, new BunLocation(Path.Combine(folder, ExecutableName), BunSources.Installed, name)));
        }

        return found;
    }

    private async Task<Result<BunLocation>> InstallAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
    {
        var version = release.Version;
        var installFolder = Path.Combine(Root, version);
        long received = 0;
        long? total = null;

        void Set(string phase, string? message, string? reason = null) =>
            Publish(progress, new BunInstallJob(phase, version, message, received, total, reason));

        if (release.AssetFor(Rid) is not { } asset)
        {
            const string message = "Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.";
            LogNoBuild(Rid);
            Set(BunInstallPhases.Failed, message, BunInstallFailures.NoBuild);
            return new FleetError(ErrorCode, message);
        }

        var downloadDirectory = Path.Combine(Root, $".download-{Guid.NewGuid():N}");
        var stagingDirectory = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");

        try
        {
            LogInstallStarting(version, Rid);
            DeleteStale();
            Directory.CreateDirectory(downloadDirectory);
            Set(BunInstallPhases.Downloading, Working);

            var archive = Path.Combine(downloadDirectory, asset.FileName);
            await DownloadAsync(release, asset, archive, (bytes, length) =>
            {
                received = bytes;
                total = length;
                Set(BunInstallPhases.Downloading, Working);
            }, ct).ConfigureAwait(false);

            Set(BunInstallPhases.Verifying, Working);
            var actual = await Sha256Async(archive, ct).ConfigureAwait(false);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                LogChecksumMismatch(asset.FileName);
                throw new InstallFailure(BunInstallFailures.Checksum,
                    $"The download didn't match Bun {version}'s checksum, so Fleet deleted it. " +
                    "Try again; if it happens again, something between you and GitHub is changing files.");
            }

            Set(BunInstallPhases.Extracting, Working);
            await Task.Run(() => Unpack(asset, archive, stagingDirectory, actual, version, ct), ct).ConfigureAwait(false);
            await PlaceAsync(asset, version, installFolder, Path.Combine(stagingDirectory, asset.Folder), ct).ConfigureAwait(false);

            LogInstalled(version, installFolder);
            Set(BunInstallPhases.Succeeded, $"Installed Bun {version}.");
            return new BunLocation(Path.Combine(installFolder, asset.ExecutableName), BunSources.Installed, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCancelled(version);
            Set(BunInstallPhases.Failed, "The install was cancelled.", BunInstallFailures.Cancelled);
            throw;
        }
        catch (InstallFailure failure)
        {
            LogFailed(version, failure.Message);
            Set(BunInstallPhases.Failed, failure.Message, failure.Reason);
            return new FleetError(ErrorCode, failure.Message);
        }
        catch (Exception ex)
        {
            LogInstallException(ex, version);
            var message = $"Couldn't install Bun {version}: {ex.Message}";
            Set(BunInstallPhases.Failed, message, BunInstallFailures.Other);
            return new FleetError(ErrorCode, message);
        }
        finally
        {
            Cleanup(downloadDirectory, stagingDirectory);
        }
    }

    private async Task DownloadAsync(
        BunRelease release, BunAsset asset, string destination, Action<long, long?> report, CancellationToken ct)
    {
        var version = release.Version;
        var host = DownloadBase.Host;
        using var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        long received = 0;
        long? total = null;
        long? declared = null;
        try
        {
            var url = release.DownloadUrl(DownloadBase, asset);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw DescribeStatus(host, response);

            declared = response.Content.Headers.ContentLength;
            total = declared ?? asset.Size;
            report(0, total);

            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            var step = Math.Min(ProgressStepBytes, Math.Max(1, (total ?? long.MaxValue) / 100));
            long reported = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                received += read;
                if (received - reported >= step)
                {
                    reported = received;
                    report(received, total);
                }
            }

            if (received != reported)
                report(received, total);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogStalled(version);
            throw received == 0
                ? new InstallFailure(BunInstallFailures.Offline, Unreachable(host, "the connection timed out."))
                : Stopped(received, total);
        }
        catch (HttpRequestException ex)
        {
            var (reason, message) = DescribeRequestFailure(ex, host, received, total);
            throw new InstallFailure(reason, message);
        }
        catch (IOException)
        {
            throw Stopped(received, total);
        }

        if (declared is { } expected && received < expected)
            throw Stopped(received, total);
    }

    private static InstallFailure DescribeStatus(string host, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        if (status is 403 or 407)
        {
            return new InstallFailure(BunInstallFailures.Blocked,
                $"{host} answered {status} {reason}, so a proxy or firewall is probably blocking downloads from GitHub.");
        }

        return new InstallFailure(BunInstallFailures.Other, $"{host} answered {status} {reason}.");
    }

    /// <summary>What a failed request means to the user: the reason, and a sentence saying so.</summary>
    internal static (string Reason, string Message) DescribeRequestFailure(HttpRequestException error, string host, long received, long? total)
    {
        if (received > 0)
        {
            var stopped = Stopped(received, total);
            return (stopped.Reason, stopped.Message);
        }

        return error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError =>
                (BunInstallFailures.Offline, Unreachable(host, $"it couldn't find {host} (DNS).")),
            HttpRequestError.ConnectionError =>
                (BunInstallFailures.Offline, Unreachable(host, "the connection was refused.")),
            HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError =>
                (BunInstallFailures.Blocked, $"{host}'s secure connection was interrupted, so a proxy or firewall is probably blocking downloads from GitHub."),
            _ => (BunInstallFailures.Offline, Unreachable(host, "the connection failed.")),
        };
    }

    private static string Unreachable(string host, string why) =>
        $"Fleet couldn't reach {host}: {why} Check that this computer is online, then try again.";

    private static InstallFailure Stopped(long received, long? total) =>
        new(BunInstallFailures.Stopped, total is { } length
            ? $"The download stopped after {Megabytes(received)} of {Megabytes(length)} MB. Try again."
            : $"The download stopped after {Megabytes(received)} MB. Try again.");

    private static string Megabytes(long bytes) =>
        Math.Round(bytes / 1_000_000d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void Unpack(BunAsset asset, string archive, string staging, string sha256, string version, CancellationToken ct)
    {
        try
        {
            SafeZipExtractor.Extract(archive, staging, ct: ct);
        }
        catch (InvalidDataException ex)
        {
            LogUnsafeArchive(version, ex.Message);
            throw new InstallFailure(BunInstallFailures.Other, $"The Bun {version} archive has an entry Fleet won't unpack: {ex.Message}");
        }

        var folder = Path.Combine(staging, asset.Folder);
        var executable = Path.Combine(folder, asset.ExecutableName);
        if (!File.Exists(executable))
            throw new InstallFailure(BunInstallFailures.Other, $"The Bun {version} archive has no {asset.Folder}/{asset.ExecutableName}.");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var manifest = new BunInstallManifest(version, asset.FileName, sha256, DateTimeOffset.UtcNow, Rid);
        File.WriteAllText(
            Path.Combine(folder, ManifestFileName),
            JsonSerializer.Serialize(manifest, BunInstallManifestJsonContext.Default.BunInstallManifest));
    }

    /// <summary>Moves the unpacked folder to <c>{root}/{version}</c>, keeping an install another Fleet finished first.</summary>
    private async Task PlaceAsync(BunAsset asset, string version, string target, string unpacked, CancellationToken ct)
    {
        if (Directory.Exists(target))
        {
            if (IsInstalled(target, version, asset.Sha256))
                return;

            // Renaming first is atomic, so another Fleet never sees a half-deleted folder; a later sweep catches a leftover.
            var aside = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");
            try
            {
                Directory.Move(target, aside);
                TryDelete(aside);
            }
            catch (IOException) when (!Directory.Exists(target))
            {
                // Another Fleet removed it first.
            }
        }

        // Antivirus scanning a fresh bun.exe can make the move fail for a moment (access denied), so try a few times.
        var delay = MoveRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                MoveDirectory(unpacked, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (IsInstalled(target, version, asset.Sha256))
                    return; // Another Fleet installed the same version between the check and the move.

                if (attempt >= MoveAttempts)
                    throw;

                LogMoveRetry(ex, attempt, MoveAttempts);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> holds a finished install of <paramref name="version"/> for this platform:
    /// a manifest naming that version (and, when given, <paramref name="sha256"/>) and the executable.
    /// </summary>
    private bool IsInstalled(string folder, string version, string? sha256)
    {
        try
        {
            var manifestPath = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(manifestPath))
                return false;

            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), BunInstallManifestJsonContext.Default.BunInstallManifest);
            return manifest is not null
                && manifest.Version == version
                && (manifest.Rid is null || manifest.Rid == Rid)
                && (sha256 is null || string.Equals(manifest.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                && File.Exists(Path.Combine(folder, ExecutableName));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void DeleteStale()
    {
        if (!Directory.Exists(Root))
            return;

        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var pattern in new[] { ".download-*", ".staging-*" })
        {
            foreach (var directory in Directory.EnumerateDirectories(Root, pattern))
            {
                if (NewestWrite(directory) < cutoff)
                    TryDelete(directory);
            }
        }
    }

    /// <summary>A folder's own time doesn't change while a file inside grows, so look at the files too.</summary>
    private static DateTime NewestWrite(string directory)
    {
        var newest = Directory.GetLastWriteTimeUtc(directory);
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.LastWriteTimeUtc > newest)
                    newest = entry.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't look inside: it may be in use, so keep it.
            return DateTime.MaxValue;
        }

        return newest;
    }

    private static void Cleanup(string downloadDirectory, string stagingDirectory)
    {
        TryDelete(downloadDirectory);
        TryDelete(stagingDirectory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next install clears what's left once it's an hour old.
        }
    }

    private void Publish(IProgress<BunInstallJob>? progress, BunInstallJob job)
    {
        Volatile.Write(ref _job, job);
        progress?.Report(job);
    }

    /// <summary>A version folder that counts as installed.</summary>
    private sealed record InstalledBun(string Name, BunVersion Version, BunLocation Location);

    /// <summary>What a probed file looked like, so a changed one is probed again.</summary>
    private sealed record ProbeKey(string Path, string? ResolvedPath, long Length, DateTime LastWriteUtc, ulong? Device, ulong? Inode);

    /// <summary>A failure with a message fit to show the user.</summary>
    private sealed class InstallFailure(string reason, string message) : Exception(message)
    {
        /// <summary>One of <see cref="BunInstallFailures"/>.</summary>
        public string Reason { get; } = reason;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet:Harness:BunDownloadBase is {Configured}, which isn't an http or https address; downloading from GitHub instead.")]
    private partial void LogInvalidDownloadBase(string configured);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing Bun {Version} for {Rid}.")]
    private partial void LogInstallStarting(string version, string rid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moving Bun into place failed (try {Attempt} of {Attempts}); trying again.")]
    private partial void LogMoveRetry(Exception ex, int attempt, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the old Bun {Version}.")]
    private partial void LogPruned(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the old Bun {Version} (it may be running); leaving it for a later prune.")]
    private partial void LogPruneSkipped(Exception ex, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed Bun {Version} at {Folder}.")]
    private partial void LogInstalled(string version, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet has no Bun build for {Rid}.")]
    private partial void LogNoBuild(string rid);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Bun download {AssetName} didn't match its pinned sha256.")]
    private partial void LogChecksumMismatch(string assetName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Bun {Version} download stalled.")]
    private partial void LogStalled(string version);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Bun {Version} archive was refused: {Reason}")]
    private partial void LogUnsafeArchive(string version, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installing Bun {Version} failed: {Reason}")]
    private partial void LogFailed(string version, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing Bun {Version} was cancelled.")]
    private partial void LogCancelled(string version);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error installing Bun {Version}.")]
    private partial void LogInstallException(Exception ex, string version);
}

/// <summary>Written beside the installed Bun so Fleet can tell a finished install from a partial one.</summary>
internal sealed record BunInstallManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("assetFileName")] string AssetFileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("installedAt")] DateTimeOffset InstalledAt,
    [property: JsonPropertyName("rid")] string? Rid = null);

[JsonSerializable(typeof(BunInstallManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class BunInstallManifestJsonContext : JsonSerializerContext
{
}
