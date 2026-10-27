using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Updates;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>
/// Hosted service that checks for a newer Fleet release on startup and periodically
/// thereafter, triggering a background download via <see cref="UpdateDownloadService"/>
/// when a newer version is found.
/// Only runs when Fleet is running from an installed package layout (VERSION file present).
/// </summary>
public sealed partial class UpdateCheckService(
    IHttpClientFactory httpClientFactory,
    FleetOptions options,
    UpdateStateHolder stateHolder,
    UpdateDownloadService downloadService,
    ReleaseNotesStore releaseNotes,
    ILogger<UpdateCheckService> logger,
    UpdateFiles? files = null) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Desktop.Enabled)
        {
            stateHolder.SetState(UpdateState.Initial with { Status = UpdateStatus.Managed });
            LogManagedByDesktop();
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

        if (options.Update.CheckOnStartup)
        {
            await CheckForUpdateAsync(stoppingToken).ConfigureAwait(false);
        }
        else
        {
            LogCheckDisabled();
        }

        // Periodic checks while running.
        var intervalHours = options.Update.CheckIntervalHours;
        if (intervalHours <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromHours(intervalHours);
        LogScheduleEnabled(intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            await CheckForUpdateAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Performs an update check and triggers download if a newer version is found.</summary>
    public async Task CheckForUpdateAsync(CancellationToken ct)
    {
        if (options.Desktop.Enabled)
            return;

        try
        {
            LogChecking(options.Update.GitHubRepo);

            using var client = CreateClient();
            // The recent releases rather than only the latest: What's new shows their notes.
            using var response = await client.GetAsync(releaseNotes.ReleasesUrl, ct).ConfigureAwait(false);

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
            var releases = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.ListGitHubReleaseDto) ?? [];
            await releaseNotes.SaveAsync(releases, ct).ConfigureAwait(false);
            var release = ReleaseNotesStore.FromGitHub(releases) is [var newest, ..] ? newest : null;

            if (release is null)
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

            var latestVersion = release.Version;
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

            // A version that failed to start here and was undone isn't fetched again; a newer one is.
            if (files?.ReadFailure() is { } failure && string.Equals(failure.To, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                stateHolder.SetState(new UpdateState(UpdateStatus.Available, latestVersion, null, null, DateTimeOffset.UtcNow, null));
                return;
            }

            var assetName = GetAssetName(latestVersion);
            var downloadUrl = DownloadUrl(options.Update, latestVersion, assetName);

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

    /// <summary>
    /// Whether <paramref name="latest"/> comes after <paramref name="current"/> in SemVer order, so a build from source
    /// (MinVer stamps <c>0.62.1-alpha.0.4</c> four commits past v0.62.0) sits after 0.62.0 and before 0.62.1.
    /// A version that can't be read is never newer: a guess could fetch, or restart into, an older Fleet.
    /// </summary>
    internal static bool IsNewerVersion(string latest, string current)
        => TryParseSemVer(latest, out var l) && TryParseSemVer(current, out var c) && CompareSemVer(l, c) > 0;

    private static bool TryParseSemVer(string? text, out (Version Core, string[] Prerelease) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Build metadata (+…) takes no part in the order.
        var value = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = value.IndexOf('-');
        var prerelease = dash < 0 ? [] : value[(dash + 1)..].Split('.');
        if (dash >= 0 && prerelease.Any(id => id.Length == 0))
            return false;

        if (!Version.TryParse(dash < 0 ? value : value[..dash], out var core))
            return false;

        // 0.62 and 0.62.0 are the same version.
        version = (new Version(core.Major, core.Minor, Math.Max(core.Build, 0)), prerelease);
        return true;
    }

    private static int CompareSemVer((Version Core, string[] Prerelease) a, (Version Core, string[] Prerelease) b)
    {
        var core = a.Core.CompareTo(b.Core);
        if (core != 0)
            return core;

        // A release comes after its prereleases.
        if (a.Prerelease.Length == 0 || b.Prerelease.Length == 0)
            return b.Prerelease.Length.CompareTo(a.Prerelease.Length);

        for (var i = 0; i < Math.Min(a.Prerelease.Length, b.Prerelease.Length); i++)
        {
            var x = a.Prerelease[i];
            var y = b.Prerelease[i];
            var xNumber = ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumber = ulong.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            var order = (xNumber, yNumber) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1, // numbers come before words
                (false, true) => 1,
                _ => string.CompareOrdinal(x, y),
            };
            if (order != 0)
                return order;
        }

        return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
    }

    internal static string DownloadUrl(UpdateOptions update, string version, string assetName) =>
        update.ReleasesBaseUrl is { Length: > 0 } feed
            ? $"{feed.TrimEnd('/')}/download/v{version}/{assetName}"
            : $"https://github.com/{update.GitHubRepo}/releases/download/v{version}/{assetName}";

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

    internal static bool IsInstalledLayout()
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

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop app updates Fleet — skipping update checks.")]
    private partial void LogManagedByDesktop();

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Periodic update checks enabled every {Hours} hour(s).")]
    private partial void LogScheduleEnabled(int hours);
}

// ── AOT-safe JSON deserialization for GitHub release response ─────────────────

internal sealed record GitHubReleaseDto(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("published_at")] string? PublishedAt = null,
    [property: JsonPropertyName("draft")] bool Draft = false,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false);

[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext
{
}
