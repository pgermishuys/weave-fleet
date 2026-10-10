using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// The Bun release Fleet wants, from the manifest it reads on its update schedule. The last good manifest is kept in
/// <c>~/.weave/runtimes/bun/bun.json</c>, so Fleet knows it offline. The Bun built into Fleet is the floor: a manifest
/// older than it, or one that lowers the oldest safe version, is ignored, so a bad or hijacked file can't roll Fleet
/// back. The schedule only runs when Fleet has a Bun to look after, so a user who never turns Mods on causes no request.
/// </summary>
internal sealed partial class BunManifestReleases(
    FleetOptions options,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<BunManifestReleases> logger) : BackgroundService, IBunReleases
{
    private const string CacheFileName = "bun.json";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private bool _loaded;
    private BunRelease? _manifest;
    private BunRelease _current = null!;

    /// <summary>Test seam: the user's home folder.</summary>
    internal string Home { get; init; } = ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory;

    /// <summary>Test seam: the release built into Fleet.</summary>
    internal BunRelease Pinned { get; init; } = BunRelease.Pinned;

    /// <summary>Test seam: whether Fleet runs from an installed package; a dev or repository layout never fetches on its schedule.</summary>
    internal Func<bool> IsInstalledLayout { get; init; } = UpdateCheckService.IsInstalledLayout;

    /// <summary>Test seam: how long one fetch may take.</summary>
    internal TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private string Root => Path.Combine(Home, ".weave", "runtimes", "bun");

    private string CachePath => Path.Combine(Root, CacheFileName);

    /// <inheritdoc />
    public BunRelease Current
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _current);
        }
    }

    /// <inheritdoc />
    public event EventHandler<BunReleaseChangedEventArgs>? Changed;

    /// <summary>Where the manifest is read from: the configured address, else <c>bun.json</c> on the update repository's main branch.</summary>
    internal static Uri UrlFor(FleetOptions options) =>
        new(string.IsNullOrWhiteSpace(options.Update.BunManifestUrl)
            ? $"https://raw.githubusercontent.com/{options.Update.GitHubRepo}/main/bun.json"
            : options.Update.BunManifestUrl.Trim());

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var bytes = await FetchAsync(ct).ConfigureAwait(false);
            if (bytes is not null)
                Accept(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A dev or repository layout, and tests, never touch the network on their own. The desktop app does fetch:
        // Bun isn't a Fleet update, so the app's hold on those doesn't apply.
        if (!IsInstalledLayout())
        {
            LogDevLayout();
            return;
        }

        try
        {
            // Let the app finish starting before it reaches for the network.
            await Task.Delay(StartupDelay, timeProvider, stoppingToken).ConfigureAwait(false);

            if (options.Update.CheckOnStartup)
                await RefreshIfNeededAsync(stoppingToken).ConfigureAwait(false);
            else
                LogCheckDisabled();

            var intervalHours = options.Update.CheckIntervalHours;
            if (intervalHours <= 0)
                return;

            var interval = TimeSpan.FromHours(intervalHours);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
                await RefreshIfNeededAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RefreshIfNeededAsync(CancellationToken ct)
    {
        if (!NeedsManifest())
        {
            LogNothingToLookAfter();
            return;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when Fleet has a Bun to look after: one configured, one installed, or a manifest already kept.</summary>
    private bool NeedsManifest()
    {
        if (!string.IsNullOrWhiteSpace(options.Harness.BunPath) || File.Exists(CachePath))
            return true;

        try
        {
            return Directory.Exists(Root)
                && Directory.EnumerateDirectories(Root).Any(folder => BunVersion.TryParse(Path.GetFileName(folder), out _));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_loaded)
                return;

            _manifest = LoadCache();
            Volatile.Write(ref _current, Choose(Pinned, _manifest));
            _loaded = true;
        }
    }

    private BunRelease? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            if (new FileInfo(CachePath).Length > BunManifest.MaxBytes)
            {
                LogCacheIgnored("it's bigger than a manifest may be");
                return null;
            }

            if (BunManifest.TryParse(File.ReadAllBytes(CachePath), out var release, out var error))
                return release;

            LogCacheIgnored(error);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheIgnored(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The manifest's release when it's as new as the pin, else the pin; either way the oldest safe version is the
    /// higher of the two, so a Fleet that raised the floor holds it against an older manifest.
    /// </summary>
    private static BunRelease Choose(BunRelease pin, BunRelease? manifest)
    {
        if (manifest is null)
            return pin;

        var oldestSafe = Max(pin.OldestSafe, manifest.OldestSafe);
        var chosen = BunVersion.Parse(manifest.Version) >= BunVersion.Parse(pin.Version) ? manifest : pin;
        return chosen.OldestSafe == oldestSafe ? chosen : chosen with { OldestSafe = oldestSafe };
    }

    private static string Max(string left, string right) =>
        BunVersion.Parse(left) >= BunVersion.Parse(right) ? left : right;

    private async Task<byte[]?> FetchAsync(CancellationToken ct)
    {
        var url = UrlFor(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using var client = httpClientFactory.CreateClient("GitHubApi");
            client.Timeout = FetchTimeout;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed((int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > BunManifest.MaxBytes)
                {
                    LogTooBig();
                    return null;
                }
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogFetchTimedOut();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            LogFetchError(ex);
            return null;
        }
    }

    private void Accept(byte[] bytes)
    {
        if (!BunManifest.TryParse(bytes, out var fetched, out var error))
        {
            LogManifestIgnored(error);
            return;
        }

        if (BunVersion.Parse(fetched.Version) < BunVersion.Parse(Pinned.Version))
        {
            LogManifestIgnored($"Bun {fetched.Version} is older than the {Pinned.Version} built into Fleet.");
            return;
        }

        var lastGood = _manifest?.OldestSafe ?? Pinned.OldestSafe;
        if (BunVersion.Parse(fetched.OldestSafe) < BunVersion.Parse(lastGood))
        {
            LogManifestIgnored($"Its oldest safe version, {fetched.OldestSafe}, is lower than the last good one, {lastGood}.");
            return;
        }

        WriteCache(bytes);
        var previous = Volatile.Read(ref _current);
        _manifest = fetched;
        var current = Choose(Pinned, fetched);
        Volatile.Write(ref _current, current);

        if (previous.Version == current.Version && previous.OldestSafe == current.OldestSafe
            && previous.Note == current.Note && previous.Assets.SequenceEqual(current.Assets))
            return;

        LogChanged(previous.Version, current.Version, current.OldestSafe);
        Raise(new BunReleaseChangedEventArgs(previous, current));
    }

    private void WriteCache(byte[] bytes)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Root);
            temporary = Path.Combine(Root, $".bun-json-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, CachePath, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheWriteFailed(ex);
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort.
                }
            }
        }
    }

    /// <summary>Calls each handler on its own, so one that throws never stops another or the service.</summary>
    private void Raise(BunReleaseChangedEventArgs args)
    {
        var handlers = Changed?.GetInvocationList();
        if (handlers is null)
            return;

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<BunReleaseChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                LogHandlerFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running in dev/repo layout, so Fleet isn't checking its Bun manifest on a schedule.")]
    private partial void LogDevLayout();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Update checks are off, so Fleet isn't checking its Bun manifest at startup.")]
    private partial void LogCheckDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fleet has no Bun to look after, so it isn't checking its Bun manifest.")]
    private partial void LogNothingToLookAfter();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring the cached Bun manifest: {Reason}")]
    private partial void LogCacheIgnored(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save the Bun manifest to the cache.")]
    private partial void LogCacheWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet's Bun manifest request returned HTTP {StatusCode}.")]
    private partial void LogFetchFailed(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet's Bun manifest request failed.")]
    private partial void LogFetchError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet's Bun manifest request timed out.")]
    private partial void LogFetchTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring the Bun manifest: it's bigger than a manifest may be.")]
    private partial void LogTooBig();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring the Bun manifest: {Reason}")]
    private partial void LogManifestIgnored(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The recommended Bun changed from {Previous} to {Current} (oldest safe {OldestSafe}).")]
    private partial void LogChanged(string previous, string current, string oldestSafe);

    [LoggerMessage(Level = LogLevel.Error, Message = "A handler for the Bun release change threw.")]
    private partial void LogHandlerFailed(Exception ex);
}
