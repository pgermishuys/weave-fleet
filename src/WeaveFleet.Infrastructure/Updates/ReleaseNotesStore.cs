using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>One Fleet release's notes, as Settings → System shows them.</summary>
public sealed record ReleaseNote(string Version, string? PublishedAt, string Body, string Url);

/// <summary>The releases Fleet knows about, newest first, and when they were fetched.</summary>
public sealed record ReleaseNotes(IReadOnlyList<ReleaseNote> Releases, DateTimeOffset? FetchedAt, string? Error);

/// <summary>
/// Keeps the notes of Fleet's recent releases in the data folder, so What's new reads them without asking GitHub
/// each time and still has them offline. The update check fills it as it goes; a Fleet that doesn't check for
/// updates (the desktop app's, or one run from source) fetches when the notes are asked for and are missing or old.
/// </summary>
public sealed partial class ReleaseNotesStore(
    IHttpClientFactory httpClientFactory,
    FleetOptions options,
    ILogger<ReleaseNotesStore> logger,
    TimeProvider? timeProvider = null) : IDisposable
{
    /// <summary>How many releases Fleet keeps: two to three weeks of releases at the usual pace.</summary>
    public const int ReleaseCount = 20;

    private const string FileName = "release-notes.json";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private ReleaseNotes? _cached;

    /// <summary>The GitHub API address of the recent releases, for the update check and for a fetch here.</summary>
    public string ReleasesUrl => $"https://api.github.com/repos/{options.Update.GitHubRepo}/releases?per_page={ReleaseCount}";

    /// <summary>The saved notes, fetched first when there are none or they're older than the update check's interval.</summary>
    public async Task<ReleaseNotes> GetAsync(CancellationToken ct)
    {
        var current = await LoadAsync(ct).ConfigureAwait(false);
        if (!IsStale(current))
            return current;

        await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have fetched while this one waited.
            current = await LoadAsync(ct).ConfigureAwait(false);
            if (!IsStale(current))
                return current;

            return await FetchAsync(current, ct).ConfigureAwait(false);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>Saves the releases GitHub listed (the update check calls this with what it fetched).</summary>
    internal async Task SaveAsync(IReadOnlyList<GitHubReleaseDto> releases, CancellationToken ct)
    {
        var notes = new ReleaseNotes(FromGitHub(releases), _time.GetUtcNow(), Error: null);
        _cached = notes;
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(notes, ReleaseNotesJsonContext.Default.ReleaseNotes), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next save tries the file again.
            LogSaveFailed(ex);
        }
    }

    /// <summary>Published releases only (no drafts or pre-releases), newest version first, at most <see cref="ReleaseCount"/>.</summary>
    internal static IReadOnlyList<ReleaseNote> FromGitHub(IEnumerable<GitHubReleaseDto> releases) =>
        releases
            .Where(r => !r.Draft && !r.Prerelease && !string.IsNullOrWhiteSpace(r.TagName))
            .Select(r => new ReleaseNote(r.TagName.TrimStart('v'), r.PublishedAt, r.Body ?? string.Empty, r.HtmlUrl))
            .OrderByDescending(r => Version.TryParse(r.Version, out var v) ? v : new Version(0, 0))
            .Take(ReleaseCount)
            .ToList();

    private async Task<ReleaseNotes> FetchAsync(ReleaseNotes current, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("GitHubApi");
            using var response = await client.GetAsync(ReleasesUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed((int)response.StatusCode);
                return current with { Error = $"GitHub returned {(int)response.StatusCode}" };
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.ListGitHubReleaseDto) ?? [];
            await SaveAsync(releases, ct).ConfigureAwait(false);
            return _cached!;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogFetchException(ex);
            return current with { Error = "Couldn't reach GitHub" };
        }
    }

    private bool IsStale(ReleaseNotes notes)
    {
        if (notes.FetchedAt is not { } fetchedAt || notes.Releases.Count == 0)
            return true;
        var hours = options.Update.CheckIntervalHours > 0 ? options.Update.CheckIntervalHours : 4;
        return _time.GetUtcNow() - fetchedAt > TimeSpan.FromHours(hours);
    }

    private async Task<ReleaseNotes> LoadAsync(CancellationToken ct)
    {
        if (_cached is not null)
            return _cached;

        var path = FilePath();
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var saved = await JsonSerializer.DeserializeAsync(stream, ReleaseNotesJsonContext.Default.ReleaseNotes, ct).ConfigureAwait(false);
                if (saved is not null)
                    return _cached = saved with { Error = null };
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LogLoadFailed(ex);
            }
        }

        return new ReleaseNotes([], FetchedAt: null, Error: null);
    }

    public void Dispose() => _fetchGate.Dispose();

    private string FilePath() =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, FileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching Fleet's release notes: GitHub returned HTTP {StatusCode}.")]
    private partial void LogFetchFailed(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't fetch Fleet's release notes.")]
    private partial void LogFetchException(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save Fleet's release notes.")]
    private partial void LogSaveFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read Fleet's saved release notes.")]
    private partial void LogLoadFailed(Exception ex);
}

[JsonSerializable(typeof(ReleaseNotes))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReleaseNotesJsonContext : JsonSerializerContext
{
}
