using System.Globalization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Files;

/// <param name="Path">From the session's folder, with <c>/</c> between names.</param>
/// <param name="Name">The sanitized name the file came with; the file on disk may have <c>-2</c> added when the name was taken.</param>
/// <param name="Lines">The number of lines when the file is text, else null.</param>
public sealed record SessionAttachment(string Path, string Name, long Size, int? Lines, string Mime);

/// <summary>
/// Files the user attaches to a prompt, written into <c>.weave/attachments/&lt;session&gt;</c> in the session's folder so the agent
/// can read them with its own tools. A <c>.gitignore</c> in <c>.weave/attachments</c> keeps them out of git.
/// </summary>
public sealed class SessionAttachments(ISessionRepository sessionRepository, ILogger<SessionAttachments> logger)
{
    public const long MaxBytes = 25L * 1024 * 1024;
    private const int MaxNameLength = 100;
    private const int BinarySniffBytes = 8 * 1024;
    private const string Folder = ".weave/attachments";

    public async Task<Result<SessionAttachment>> SaveAsync(
        string sessionId,
        string? originalName,
        string? contentType,
        Stream content,
        long? contentLength = null,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");
        if (string.IsNullOrWhiteSpace(session.Directory) || !Directory.Exists(session.Directory))
            return FleetError.ValidationError("Session.Directory", "The session's folder is gone.");

        var name = SanitizeName(originalName);
        if (name is null)
            return FleetError.ValidationError("Attachment.Name", "The file needs a name.");

        if (contentLength > MaxBytes)
            return TooBig(name, contentLength.Value);

        var key = new string(session.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(8).ToArray());
        var weave = Path.Combine(session.Directory, ".weave");
        var root = Path.Combine(weave, "attachments");
        var folder = Path.Combine(root, key);
        if (AnyLink(weave, root, folder))
            return LinkError();
        Directory.CreateDirectory(folder);
        if (AnyLink(weave, root, folder))
            return LinkError();

        // The folder ignores itself, so git never sees an attachment.
        var ignore = Path.Combine(root, ".gitignore");
        if (!File.Exists(ignore))
            await File.WriteAllTextAsync(ignore, "*\n", ct).ConfigureAwait(false);

        var (stream, fileName) = Create(folder, name);
        long size = 0;
        long newlines = 0;
        var lastByte = (byte)'\n';
        var binary = false;
        var tooBig = false;
        try
        {
            await using (stream)
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (size + read > MaxBytes)
                    {
                        size += read;
                        tooBig = true;
                        break;
                    }

                    var chunk = buffer.AsSpan(0, read);
                    if (size < BinarySniffBytes && chunk[..(int)Math.Min(read, BinarySniffBytes - size)].Contains((byte)0))
                        binary = true;
                    newlines += chunk.Count((byte)'\n');
                    lastByte = chunk[^1];
                    size += read;
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(Path.Combine(folder, fileName));
            throw;
        }

        if (tooBig)
        {
            TryDelete(Path.Combine(folder, fileName));
            return TooBig(name, size);
        }

        int? lines = binary ? null : checked((int)(newlines + (size > 0 && lastByte != '\n' ? 1 : 0)));
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        return new SessionAttachment($"{Folder}/{key}/{fileName}", name, size, lines, mime);
    }

    /// <summary>Letters, digits, dot, dash and underscore; no leading dot; at most 100 characters, the extension kept.</summary>
    internal static string? SanitizeName(string? name)
    {
        name = System.IO.Path.GetFileName((name ?? "").Trim().Replace('\\', '/'));
        var clean = new string(name.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' ? c : '-').ToArray())
            .TrimStart('.');
        if (clean.Length == 0)
            return null;
        if (clean.Length > MaxNameLength)
        {
            var ext = System.IO.Path.GetExtension(clean);
            if (ext.Length > 20)
                ext = ext[..20];
            clean = clean[..(MaxNameLength - ext.Length)] + ext;
        }

        return clean;
    }

    /// <summary>Opens a new file named <paramref name="name"/>; a name already taken becomes <c>name-2.ext</c>, <c>name-3.ext</c>.</summary>
    private static (FileStream Stream, string FileName) Create(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var fileName = n == 1 ? name : $"{stem}-{n}{ext}";
            try
            {
                return (new FileStream(Path.Combine(folder, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None), fileName);
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, fileName)))
            {
            }
        }
    }

    private static bool AnyLink(params string[] paths) => paths.Any(p => new DirectoryInfo(p).LinkTarget is not null);

    private static Result<SessionAttachment> LinkError() =>
        FleetError.ValidationError("Session.Directory", "An attachments folder is a link, so Fleet won't write there.");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }

    private static Result<SessionAttachment> TooBig(string name, long bytes)
    {
        var mb = (long)Math.Ceiling(bytes / (1024.0 * 1024));
        return FleetError.ValidationError("Attachment.Size", $"{name} is {mb.ToString(CultureInfo.InvariantCulture)} MB. The limit is 25 MB.");
    }
}
