using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Downloads and validates a Fleet release archive to a staging directory.
/// The update is not applied here — the launcher scripts apply it on next start.
/// </summary>
public sealed partial class UpdateDownloadService(
    IHttpClientFactory httpClientFactory,
    UpdateStateHolder stateHolder,
    ILogger<UpdateDownloadService> logger)
{
    private static readonly SemaphoreSlim s_downloadLock = new(1, 1);

    /// <summary>Downloads the update described by <paramref name="state"/> and stages it for the next launch.</summary>
    public async Task DownloadUpdateAsync(UpdateState state, CancellationToken ct)
    {
        if (state.DownloadUrl is null || state.AssetName is null || state.LatestVersion is null)
            return;

        if (!await s_downloadLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            LogDownloadAlreadyInProgress();
            return;
        }

        try
        {
            await DownloadUpdateCoreAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            s_downloadLock.Release();
        }
    }

    private async Task DownloadUpdateCoreAsync(UpdateState state, CancellationToken ct)
    {
        // Null checks done by caller (DownloadUpdateAsync).
        var latestVersion = state.LatestVersion!;
        var assetName = state.AssetName!;
        var downloadUrl = state.DownloadUrl!;

        var stagingDir = GetStagingDirectory();

        try
        {
            stateHolder.SetState(state with { Status = UpdateStatus.Downloading });
            LogDownloadStarting(latestVersion, stagingDir);

            Directory.CreateDirectory(stagingDir);

            var archivePath = Path.Combine(stagingDir, assetName);
            var checksumPath = archivePath + ".sha256";
            var checksumUrl = downloadUrl + ".sha256";

            using var client = httpClientFactory.CreateClient("GitHubApi");

            // Download the archive.
            await DownloadFileAsync(client, downloadUrl, archivePath, ct).ConfigureAwait(false);
            LogArchiveDownloaded(assetName);

            // Download the checksum file.
            await DownloadFileAsync(client, checksumUrl, checksumPath, ct).ConfigureAwait(false);

            // Validate checksum.
            var expectedHash = await ReadExpectedHashAsync(checksumPath, assetName, ct).ConfigureAwait(false);
            if (expectedHash is null)
            {
                LogChecksumFileMissing(assetName);
                SetError(state, "Checksum file did not contain expected entry.");
                return;
            }

            var actualHash = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LogChecksumMismatch(assetName);
                SetError(state, "SHA256 checksum mismatch — download may be corrupted.");
                return;
            }

            LogChecksumVerified(assetName);

            // Write the manifest so the launcher knows what to apply.
            var manifest = new UpdateManifest(latestVersion, assetName, actualHash, DateTimeOffset.UtcNow);
            var manifestPath = Path.Combine(stagingDir, "update-manifest.json");
            var manifestJson = JsonSerializer.Serialize(manifest, UpdateManifestJsonContext.Default.UpdateManifest);
            await File.WriteAllTextAsync(manifestPath, manifestJson, ct).ConfigureAwait(false);

            stateHolder.SetState(state with { Status = UpdateStatus.Staged });
            LogStagingComplete(latestVersion, stagingDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App is shutting down — clean up partial download.
            TryCleanupStagingDir(stagingDir);
        }
        catch (Exception ex)
        {
            LogDownloadException(ex);
            TryCleanupStagingDir(stagingDir);
            SetError(state, ex.Message);
        }
    }

    private static string GetStagingDirectory()
    {
        // The app binary lives at <install>/app/. Stage one level up, alongside the app dir.
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Path.GetDirectoryName(appDir);
        if (installRoot is not null)
            return Path.Combine(installRoot, "update");

        // Fallback: use temp directory.
        return Path.Combine(Path.GetTempPath(), "fleet-update");
    }

    private void SetError(UpdateState state, string message) =>
        stateHolder.SetState(state with { Status = UpdateStatus.Error, Error = message });

    private static async Task DownloadFileAsync(HttpClient client, string url, string destPath, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = File.Create(destPath);
        await stream.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadExpectedHashAsync(string checksumPath, string assetName, CancellationToken ct)
    {
        if (!File.Exists(checksumPath))
            return null;

        // Format: "<hash>  <filename>" or just "<hash>"
        var lines = await File.ReadAllLinesAsync(checksumPath, ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // If the line contains the filename, match on it.
            if (trimmed.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                return trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];

            // Single-hash file (per-asset .sha256 files).
            if (trimmed.Length == 64 && trimmed.All(c => Uri.IsHexDigit(c)))
                return trimmed;
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void TryCleanupStagingDir(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading Fleet v{Version} to staging directory {Dir}.")]
    private partial void LogDownloadStarting(string version, string dir);

    [LoggerMessage(Level = LogLevel.Information, Message = "Archive downloaded: {AssetName}.")]
    private partial void LogArchiveDownloaded(string assetName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checksum file did not contain an entry for {AssetName}.")]
    private partial void LogChecksumFileMissing(string assetName);

    [LoggerMessage(Level = LogLevel.Error, Message = "SHA256 checksum mismatch for {AssetName}.")]
    private partial void LogChecksumMismatch(string assetName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Checksum verified for {AssetName}.")]
    private partial void LogChecksumVerified(string assetName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet v{Version} staged at {Dir}. Restart to apply.")]
    private partial void LogStagingComplete(string version, string dir);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error during update download.")]
    private partial void LogDownloadException(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download already in progress, skipping concurrent request.")]
    private partial void LogDownloadAlreadyInProgress();
}

// ── Update manifest ────────────────────────────────────────────────────────────

/// <summary>Written to the staging directory so the launcher knows what to apply.</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("assetFileName")] string AssetFileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("downloadedAt")] DateTimeOffset DownloadedAt);

[JsonSerializable(typeof(UpdateManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UpdateManifestJsonContext : JsonSerializerContext
{
}
