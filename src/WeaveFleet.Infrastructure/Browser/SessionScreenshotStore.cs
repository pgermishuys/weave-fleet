using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Files under <c>{root}/{sessionId}/{screenshotId}.png</c>. Ids are Fleet's own (<c>shot_</c> and a ULID), and
/// both parts of a path are checked before they touch the disk, so a request can't read outside the folder.
/// A shot is kept for <see cref="Retention"/>: long enough to look back at a session's work, short enough that
/// a busy agent's pictures don't pile up on disk.
/// </summary>
internal sealed partial class SessionScreenshotStore(string root, ILogger<SessionScreenshotStore> logger) : ISessionScreenshotStore
{
    /// <summary>How long a screenshot is kept after it was taken.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const string Prefix = "shot_";
    private const string Extension = ".png";

    public async Task<string?> SaveAsync(string sessionId, byte[] png, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!IsSafe(sessionId))
            return null;

        var id = Prefix + Ulid.NewUlid();
        var path = Path.Combine(root, sessionId, id + Extension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex, sessionId);
            return null;
        }
    }

    public async Task<byte[]?> ReadAsync(string sessionId, string screenshotId, CancellationToken ct = default)
    {
        if (!IsSafe(sessionId) || !screenshotId.StartsWith(Prefix, StringComparison.Ordinal) || !IsSafe(screenshotId))
            return null;

        try
        {
            return await File.ReadAllBytesAsync(Path.Combine(root, sessionId, screenshotId + Extension), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!IsSafe(sessionId))
            return Task.CompletedTask;

        try
        {
            Directory.Delete(Path.Combine(root, sessionId), recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes the screenshots taken before <paramref name="cutoff"/>, and the session folders that leaves empty.
    /// Returns how many were deleted. A file that can't be deleted now is tried again next time.
    /// </summary>
    public int DeleteOlderThan(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(root))
            return 0;

        var deleted = 0;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*" + Extension))
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime)
                        continue;
                    File.Delete(file);
                    deleted++;
                }

                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailed(ex, folder);
            }
        }
        return deleted;
    }

    private static bool IsSafe(string? id) => !string.IsNullOrEmpty(id) && SafeId().IsMatch(id) && id is not ("." or "..");

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex SafeId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't keep a screenshot for session {SessionId}; the conversation won't show it")]
    private partial void LogSaveFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't clear old screenshots in {Folder}; trying again later")]
    private partial void LogCleanupFailed(Exception ex, string folder);
}
