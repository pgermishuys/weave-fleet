using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Saving a file from the editor. A save carries the hash of the file as it was last read, so it
/// never overwrites a change the agent made in between.
/// </summary>
public sealed partial class SessionOrchestrator
{
    /// <summary>Files larger than this open read-only, and a save may not grow a file past it.</summary>
    public const int MaxEditableFileBytes = 512 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileWriteLocks = new(StringComparer.Ordinal);

    /// <summary>SHA-256 of the bytes as lowercase hex, the version a save is checked against.</summary>
    public static string HashFileBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Writes <paramref name="content"/> to an existing text file in the session directory, if the file
    /// still has <paramref name="baseHash"/>. Otherwise nothing is written and the result carries the
    /// file's current content and hash.
    /// </summary>
    public async Task<Result<WriteFileResult>> WriteSessionFileAsync(
        string sessionId,
        string? path,
        string? content,
        string? baseHash,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Session.File", "Path parameter is required.");
        if (content is null)
            return FleetError.ValidationError("Session.File", "Content is required.");
        if (string.IsNullOrWhiteSpace(baseHash))
            return FleetError.ValidationError("Session.File", "The hash of the file as it was read is required.");

        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (!Directory.Exists(session.Directory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var sessionDirectory = Path.GetFullPath(session.Directory);
        var targetPath = Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));
        if (!IsSameOrChildPath(targetPath, sessionDirectory) || PathsEqual(targetPath, sessionDirectory))
            return FleetError.ValidationError("Session.File", "Path traversal is not allowed.");

        // A symlink may point anywhere; compare where the file really is with where the session really is.
        var realSessionDirectory = ResolveRealPath(sessionDirectory);
        var realTargetPath = ResolveRealPath(targetPath);
        if (!IsSameOrChildPath(realTargetPath, realSessionDirectory))
            return FleetError.ValidationError("Session.File", "The file links outside the session directory.");

        if (IsInsideGitDirectory(Path.GetRelativePath(sessionDirectory, targetPath))
            || IsInsideGitDirectory(Path.GetRelativePath(realSessionDirectory, realTargetPath)))
            return FleetError.ValidationError("Session.File", "Files under .git can't be edited.");

        if (!File.Exists(realTargetPath))
            return FleetError.NotFoundFor("File", path);

        var newBytes = StrictUtf8.GetBytes(content);
        if (newBytes.Length > MaxEditableFileBytes)
            return FleetError.ValidationError("Session.File", "The file would be too large to edit here (over 512 KB).");

        var gate = FileWriteLocks.GetOrAdd(realTargetPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] currentBytes;
            try
            {
                if (new FileInfo(realTargetPath).Length > MaxEditableFileBytes)
                    return FleetError.ValidationError("Session.File", "Too large to edit here (over 512 KB).");
                currentBytes = await File.ReadAllBytesAsync(realTargetPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FleetError.NotFoundFor("File", path);
            }

            var currentText = DecodeText(currentBytes);
            if (currentText is null)
                return FleetError.ValidationError("Session.File", "Binary files can't be edited.");

            var currentHash = HashFileBytes(currentBytes);
            if (!string.Equals(currentHash, baseHash, StringComparison.OrdinalIgnoreCase))
                return new WriteFileResult(Saved: false, currentHash, currentText);

            // Truncate and write the existing file rather than replacing it, so its inode, owner and
            // mode (such as +x) stay as they are.
            await using (var stream = new FileStream(realTargetPath, FileMode.Truncate, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync(newBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            var newHash = HashFileBytes(newBytes);
            var relativePath = Path.GetRelativePath(sessionDirectory, targetPath).Replace(Path.DirectorySeparatorChar, '/');
            LogFileSaved(logger, sessionId, relativePath, session.UserId, newBytes.Length);
            await BroadcastFileSavedAsync(sessionId, session.UserId, relativePath, ct).ConfigureAwait(false);
            return new WriteFileResult(Saved: true, newHash, CurrentContent: null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The text of a file, or null when it's binary or not valid UTF-8 (the read endpoint's rule).</summary>
    private static string? DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().Contains((byte)0))
            return null;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsInsideGitDirectory(string relativePath) =>
        relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path with every symlink along it followed, like realpath(3). Parts that don't exist are
    /// kept as they are.
    /// </summary>
    private static string ResolveRealPath(string fullPath, int depth = 0)
    {
        if (depth > 40)
            return fullPath;

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        var parts = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    next = ResolveRealPath(Path.GetFullPath(target.FullName), depth + 1);
            }

            current = next;
        }

        return current;
    }

    private Task BroadcastFileSavedAsync(string sessionId, string userId, string relativePath, CancellationToken ct)
    {
        var payload = new FilesChangedPayload
        {
            SessionId = sessionId,
            Files = [new FileChangeEntry { Path = relativePath, ChangeType = "change" }],
        };

        return eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            "files.changed",
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.FilesChangedPayload),
            new FilesChanged { Payload = payload },
            userId,
            ct);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Saved file {Path} in session {SessionId} for user {UserId} ({Bytes} bytes)")]
    private static partial void LogFileSaved(ILogger logger, string sessionId, string path, string userId, int bytes);
}

/// <summary>
/// The outcome of a save. When <see cref="Saved"/> is false the file changed since it was read:
/// <see cref="Hash"/> and <see cref="CurrentContent"/> are what's on disk now.
/// </summary>
public sealed record WriteFileResult(bool Saved, string Hash, string? CurrentContent);
