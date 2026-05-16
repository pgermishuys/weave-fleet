using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Hosted service that checks for a newer Fleet release on startup and, when found,
/// triggers a background download via <see cref="UpdateDownloadService"/>.
/// Only runs when Fleet is running from an installed package layout (VERSION file present).
/// </summary>
public sealed partial class UpdateCheckService(
    IHttpClientFactory httpClientFactory,
    FleetOptions options,
    UpdateStateHolder stateHolder,
    UpdateDownloadService downloadService,
    ILogger<UpdateCheckService> logger) : BackgroundService
{
    private const string GitHubApiBase = "https://api.github.com";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Update.CheckOnStartup)
        {
            LogCheckDisabled();
            return;
        }

        // Only run update checks from an installed layout (VERSION file exists next to the binary).
        if (!IsInstalledLayout())
        {
            LogDevLayout();
            return;
        }

        // Short delay so the app finishes startup before hitting the network.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        await CheckForUpdateAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Performs an update check and triggers download if a newer version is found.</summary>
    public async Task CheckForUpdateAsync(CancellationToken ct)
    {
        try
        {
            LogChecking(options.Update.GitHubRepo);

            using var client = CreateClient();
            var url = $"{GitHubApiBase}/repos/{options.Update.GitHubRepo}/releases/latest";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogCheckFailed((int)response.StatusCode);
                stateHolder.SetState(new UpdateState(
                    UpdateStatus.Error,
                    LatestVersion: null,
                    DownloadUrl: null,
                    AssetName: null,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: $"GitHub API returned {(int)response.StatusCode}"));
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.GitHubReleaseDto);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                LogInvalidResponse();
                stateHolder.SetState(new UpdateState(
                    UpdateStatus.Error,
                    LatestVersion: null,
                    DownloadUrl: null,
                    AssetName: null,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: "Invalid response from GitHub API"));
                return;
            }

            var latestVersion = release.TagName.TrimStart('v');
            var currentVersion = FleetInstrumentation.ServiceVersion.Split('+')[0].TrimStart('v');

            LogVersionFound(currentVersion, latestVersion);

            if (!IsNewerVersion(latestVersion, currentVersion))
            {
                stateHolder.SetState(new UpdateState(
                    UpdateStatus.UpToDate,
                    LatestVersion: latestVersion,
                    DownloadUrl: null,
                    AssetName: null,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: null));
                return;
            }

            var assetName = GetAssetName(latestVersion);
            var downloadUrl = $"https://github.com/{options.Update.GitHubRepo}/releases/download/v{latestVersion}/{assetName}";

            var available = new UpdateState(
                UpdateStatus.Available,
                LatestVersion: latestVersion,
                DownloadUrl: downloadUrl,
                AssetName: assetName,
                CheckedAt: DateTimeOffset.UtcNow,
                Error: null);
            stateHolder.SetState(available);

            LogUpdateAvailable(latestVersion);

            // Trigger background download automatically.
            await downloadService.DownloadUpdateAsync(available, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App is shutting down — ignore.
        }
        catch (Exception ex)
        {
            LogCheckException(ex);
            stateHolder.SetState(new UpdateState(
                UpdateStatus.Error,
                LatestVersion: null,
                DownloadUrl: null,
                AssetName: null,
                CheckedAt: DateTimeOffset.UtcNow,
                Error: ex.Message));
        }
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest))
            return false;

        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;

        // Fall back to string comparison if parsing fails.
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetAssetName(string version)
    {
        var rid = GetRuntimeIdentifier();
        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zip" : "tar.gz";
        return $"fleet-v{version}-{rid}.{ext}";
    }

    internal static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "linux-x64";
    }

    private static bool IsInstalledLayout()
    {
        // The app binary lives at <install>/app/WeaveFleet.Api[.exe].
        // The VERSION file lives at <install>/VERSION (one level above app/).
        var appDir = AppContext.BaseDirectory;
        var installRoot = Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (installRoot is null) return false;
        return File.Exists(Path.Combine(installRoot, "VERSION"));
    }

    private HttpClient CreateClient() => httpClientFactory.CreateClient("GitHubApi");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Update checks are disabled via configuration.")]
    private partial void LogCheckDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running in dev/repo layout — skipping update check.")]
    private partial void LogDevLayout();

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking for Fleet updates from {Repo}.")]
    private partial void LogChecking(string repo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub releases API returned HTTP {StatusCode}.")]
    private partial void LogCheckFailed(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received invalid or empty release response from GitHub.")]
    private partial void LogInvalidResponse();

    [LoggerMessage(Level = LogLevel.Information, Message = "Current version: {Current}. Latest release: {Latest}.")]
    private partial void LogVersionFound(string current, string latest);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update available: v{Version}. Starting background download.")]
    private partial void LogUpdateAvailable(string version);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error during update check.")]
    private partial void LogCheckException(Exception ex);
}

// ── AOT-safe JSON deserialization for GitHub release response ─────────────────

internal sealed record GitHubReleaseDto(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body);

[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext
{
}
