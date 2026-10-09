using System.Collections.Frozen;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Sessions.Files;

/// <summary>
/// Images in the session's folder, which a file tab shows as a picture instead of opening in the editor.
/// </summary>
public sealed partial class SessionFiles
{
    /// <summary>The image types a file tab shows, by extension, with the content type they're served as.</summary>
    public static readonly FrozenDictionary<string, string> ImageContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where an image in the session's folder really is, and its content type. Only the types in
    /// <see cref="ImageContentTypes"/>, and only files that are in the session's folder after following links.
    /// </summary>
    public async Task<Result<SessionImage>> ResolveSessionImageAsync(string sessionId, string? path)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Session.File", "Path parameter is required.");
        if (!ImageContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
            return FleetError.ValidationError("Session.File", "Only images can be read this way.");

        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (!Directory.Exists(session.Directory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var sessionDirectory = Path.GetFullPath(session.Directory);
        var targetPath = Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));
        if (!IsSameOrChildPath(targetPath, sessionDirectory))
            return FleetError.ValidationError("Session.File", "Path traversal is not allowed.");

        // A symlink may point anywhere; compare where the file really is with where the session really is.
        var realTargetPath = ResolveRealPath(targetPath);
        if (!IsSameOrChildPath(realTargetPath, ResolveRealPath(sessionDirectory)))
            return FleetError.ValidationError("Session.File", "The file links outside the session directory.");

        if (!File.Exists(realTargetPath))
            return FleetError.NotFoundFor("File", path);

        return new SessionImage(realTargetPath, contentType);
    }
}

/// <summary>An image in a session's folder: its full path on disk and the content type it's served as.</summary>
public sealed record SessionImage(string FullPath, string ContentType);
