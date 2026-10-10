using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Common;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Finds the Bun the mod host runs on and, when there's none, downloads the pinned release, checks its sha256 and
/// installs it under <c>~/.weave/runtimes/bun/{version}/</c>. The archive is unpacked next to its final place and
/// moved in whole, so a half-finished install is never mistaken for a good one.
/// </summary>
internal sealed partial class BunRuntimeInstaller(
    FleetOptions options,
    IHostEnvironment environment,
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
    private BunInstallJob? _job;
    private Func<string?>? _findOnPath;

    /// <summary>Test seam: the user's home folder.</summary>
    internal string Home { get; init; } = ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory;

    /// <summary>Test seam: the release to install.</summary>
    internal BunRelease Release { get; init; } = BunRelease.Pinned;

    /// <summary>Test seam: the platform to install for.</summary>
    internal string Rid { get; init; } = BunRelease.CurrentRid();

    /// <summary>Test seam: where releases are downloaded from.</summary>
    internal Uri DownloadBase { get; init; } = BunRelease.GitHubDownloads;

    /// <summary>Test seam: how long a download may go without a byte before it fails.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Test seam: moves the unpacked folder into place.</summary>
    internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;

    /// <summary>Test seam: the wait before the second try of the final move; each later wait doubles.</summary>
    internal TimeSpan MoveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Test seam: finds <c>bun</c> on <c>PATH</c>.</summary>
    internal Func<string?> FindOnPath
    {
        get => _findOnPath ?? DefaultFindOnPath;
        init => _findOnPath = value;
    }

    /// <inheritdoc />
    public string Version => Release.Version;

    /// <inheritdoc />
    public BunInstallJob? Job => Volatile.Read(ref _job);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private string Root => Path.Combine(Home, ".weave", "runtimes", "bun");

    private string InstallFolder => Path.Combine(Root, Release.Version);

    private string? DefaultFindOnPath() =>
        ExecutableResolver.TryResolve("bun", [Path.Combine(Home, ".bun", "bin")], out var path) ? path : null;

    /// <inheritdoc />
    public BunLocation? Find()
    {
        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath))
        {
            var configured = options.Harness.BunPath;
            return Path.IsPathFullyQualified(configured) && File.Exists(configured)
                ? new BunLocation(configured, BunSources.Configured, null)
                : null;
        }

        if (Release.AssetFor(Rid) is { } asset && IsInstalled(InstallFolder, asset))
            return new BunLocation(Path.Combine(InstallFolder, asset.ExecutableName), BunSources.Installed, Release.Version);

        if (environment.IsDevelopment() && FindOnPath() is { } onPath)
            return new BunLocation(Path.GetFullPath(onPath), BunSources.Path, null);

        return null;
    }

    /// <inheritdoc />
    public async Task<Result<BunLocation>> EnsureAsync(IProgress<BunInstallJob>? progress, CancellationToken ct)
    {
        var configured = options.Harness.BunPath;
        if (!string.IsNullOrWhiteSpace(configured) && Find() is null)
        {
            return new FleetError(ErrorCode, Path.IsPathFullyQualified(configured)
                ? $"Fleet:Harness:BunPath is {configured}, which doesn't exist."
                : $"Fleet:Harness:BunPath must be an absolute path; it's {configured}.");
        }

        if (Find() is { } found)
            return found;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Find() is { } installedMeanwhile)
                return installedMeanwhile;

            return await InstallAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<BunLocation>> InstallAsync(IProgress<BunInstallJob>? progress, CancellationToken ct)
    {
        var version = Release.Version;
        long received = 0;
        long? total = null;

        void Set(string phase, string? message) =>
            Publish(progress, new BunInstallJob(phase, version, message, received, total));

        if (Release.AssetFor(Rid) is not { } asset)
        {
            var message = $"Fleet has no Bun build for {Rid}. Install Bun and set Fleet:Harness:BunPath to it.";
            LogNoBuild(Rid);
            Set(BunInstallPhases.Failed, message);
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
            await DownloadAsync(asset, archive, (bytes, length) =>
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
                throw new InstallFailure($"The Bun {version} download didn't match its checksum, so Fleet deleted it.");
            }

            Set(BunInstallPhases.Extracting, Working);
            await Task.Run(() => Unpack(asset, archive, stagingDirectory, actual, version, ct), ct).ConfigureAwait(false);
            await PlaceAsync(asset, Path.Combine(stagingDirectory, asset.Folder), ct).ConfigureAwait(false);

            LogInstalled(version, InstallFolder);
            Set(BunInstallPhases.Succeeded, $"Installed Bun {version}.");
            return new BunLocation(Path.Combine(InstallFolder, asset.ExecutableName), BunSources.Installed, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCancelled(version);
            Set(BunInstallPhases.Failed, "The install was cancelled.");
            throw;
        }
        catch (InstallFailure failure)
        {
            LogFailed(version, failure.Message);
            Set(BunInstallPhases.Failed, failure.Message);
            return new FleetError(ErrorCode, failure.Message);
        }
        catch (Exception ex)
        {
            LogInstallException(ex, version);
            var message = $"Couldn't install Bun {version}: {ex.Message}";
            Set(BunInstallPhases.Failed, message);
            return new FleetError(ErrorCode, message);
        }
        finally
        {
            Cleanup(downloadDirectory, stagingDirectory);
        }
    }

    private async Task DownloadAsync(
        BunAsset asset, string destination, Action<long, long?> report, CancellationToken ct)
    {
        var version = Release.Version;
        using var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        long received = 0;
        long? total = null;
        try
        {
            var url = Release.DownloadUrl(DownloadBase, asset);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InstallFailure($"Couldn't download Bun {version}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            total = response.Content.Headers.ContentLength;
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
            throw new InstallFailure($"Couldn't download Bun {version}: nothing arrived for {StallTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw Stopped(version, received, total, ex);
        }

        if (total is { } expected && received < expected)
            throw Stopped(version, received, total, null);
    }

    private static InstallFailure Stopped(string version, long received, long? total, Exception? cause) =>
        new(total is { } length
            ? $"Couldn't download Bun {version}: the download stopped after {received} of {length} bytes."
            : $"Couldn't download Bun {version}: {cause?.Message ?? $"the download stopped after {received} bytes."}");

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
            throw new InstallFailure($"The Bun {version} archive has an entry Fleet won't unpack: {ex.Message}");
        }

        var folder = Path.Combine(staging, asset.Folder);
        var executable = Path.Combine(folder, asset.ExecutableName);
        if (!File.Exists(executable))
            throw new InstallFailure($"The Bun {version} archive has no {asset.Folder}/{asset.ExecutableName}.");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var manifest = new BunInstallManifest(version, asset.FileName, sha256, DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(folder, ManifestFileName),
            JsonSerializer.Serialize(manifest, BunInstallManifestJsonContext.Default.BunInstallManifest));
    }

    /// <summary>Moves the unpacked folder to <c>{root}/{version}</c>, keeping an install another Fleet finished first.</summary>
    private async Task PlaceAsync(BunAsset asset, string unpacked, CancellationToken ct)
    {
        var target = InstallFolder;
        if (Directory.Exists(target))
        {
            if (IsInstalled(target, asset))
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
                if (IsInstalled(target, asset))
                    return; // Another Fleet installed the same version between the check and the move.

                if (attempt >= MoveAttempts)
                    throw;

                LogMoveRetry(ex, attempt, MoveAttempts);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private bool IsInstalled(string folder, BunAsset asset)
    {
        try
        {
            var manifestPath = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(manifestPath))
                return false;

            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), BunInstallManifestJsonContext.Default.BunInstallManifest);
            return manifest is not null
                && manifest.Version == Release.Version
                && string.Equals(manifest.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(folder, asset.ExecutableName));
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
            // Can't look inside: go by the folder's own time.
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

    /// <summary>A failure with a message fit to show the user.</summary>
    private sealed class InstallFailure(string message) : Exception(message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing Bun {Version} for {Rid}.")]
    private partial void LogInstallStarting(string version, string rid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moving Bun into place failed (try {Attempt} of {Attempts}); trying again.")]
    private partial void LogMoveRetry(Exception ex, int attempt, int attempts);

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
    [property: JsonPropertyName("installedAt")] DateTimeOffset InstalledAt);

[JsonSerializable(typeof(BunInstallManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class BunInstallManifestJsonContext : JsonSerializerContext
{
}
