using System.Globalization;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// The <c>fleet_session_read</c> tool, for calls from a harness process: one page of another session's conversation as
/// short text, newest page first, for an agent the user pointed at that session with <c>@</c>. The caller is the
/// session the call resolves to through <see cref="IHarnessCanvasCallerResolver"/>, and only its owner's sessions can be
/// read: the call runs as that user, and the repository finds no one else's.
/// </summary>
public sealed class SessionReadBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ISessionRepository sessions,
    ISessionMessageProxy messages,
    IHarnessRegistry harnessRegistry,
    SessionActivityTracker activityTracker)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    /// <summary>The most of one message's text the page quotes; the rest is cut with a note saying how much.</summary>
    public const int MaxMessageLength = 2000;

    public async Task<CanvasResult<CanvasToolOutput>> ReadAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? sessionId,
        string? before,
        int? limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);
        if (caller is null)
            return UnknownCaller();

        if (string.IsNullOrWhiteSpace(sessionId))
            return Invalid("\"sessionId\" is required: the id of a session in a <fleet-session-references> block.");
        if (limit is < 1 or > MaxLimit)
            return Invalid($"\"limit\" is from 1 to {MaxLimit}.");

        using (userScope.Begin(caller.UserId))
        {
            var session = await sessions.GetByIdAsync(sessionId.Trim()).ConfigureAwait(false);
            if (session is null)
            {
                return CanvasResult.Fail<CanvasToolOutput>(
                    CanvasErrorKind.NotFound,
                    $"No session {sessionId}. Use the id of a session in a <fleet-session-references> block.");
            }

            var cursor = string.IsNullOrWhiteSpace(before) ? null : before.Trim();
            var page = await messages.GetMessagesAsync(session.Id, limit ?? DefaultLimit, cursor, ct).ConfigureAwait(false);

            var output = new StringBuilder();
            var harness = harnessRegistry.GetByType(session.HarnessType)?.DisplayName ?? session.HarnessType;
            var status = string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal)
                ? "archived"
                : activityTracker.GetEffectiveActivityStatus(session.Id) ?? session.ActivityStatus ?? "idle";
            output.Append("Session \"").Append(session.Title).Append("\" (").Append(session.Id).Append(") · ")
                .Append(harness).Append(" · ").Append(status).Append(" · ").Append(session.Directory).Append('\n');

            if (page.Messages.Count == 0)
            {
                output.Append(cursor is null ? "It has no messages yet." : "There are no messages before that one.");
                return CanvasResult.Ok(new CanvasToolOutput($"Read {session.Title}", output.ToString()));
            }

            output.Append(cursor is null ? "Its latest " : "The ").Append(page.Messages.Count)
                .Append(page.Messages.Count == 1 ? " message" : " messages")
                .Append(cursor is null ? "" : " before that")
                .Append(", oldest first.");
            var next = page.HasMore ? page.Cursor ?? page.Messages[0].Id : null;
            output.Append(next is null
                ? " That's the start of the session.\n"
                : $" For older ones, call again with before \"{next}\".\n");

            foreach (var message in page.Messages)
                AppendMessage(output, message);

            return CanvasResult.Ok(new CanvasToolOutput($"Read {session.Title}", output.ToString().TrimEnd()));
        }
    }

    private static void AppendMessage(StringBuilder output, HarnessMessage message)
    {
        output.Append("\n[").Append(message.Role).Append(' ')
            .Append(message.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(' ').Append(message.Id).Append("]\n");

        var text = message.TextContent.Trim();
        if (text.Length > MaxMessageLength)
        {
            output.Append(text.AsSpan(0, MaxMessageLength))
                .Append("… (").Append(text.Length - MaxMessageLength).Append(" more characters)\n");
        }
        else if (text.Length > 0)
        {
            output.Append(text).Append('\n');
        }

        // Tool calls as one line each: what was called and how it went. Their output stays in that session.
        foreach (var tool in message.Parts.OfType<ToolUsePart>())
        {
            output.Append("· ").Append(tool.ToolName);
            if (Describe(tool) is { } what)
                output.Append(": ").Append(what);
            output.Append(" (").Append(tool.State.ToString().ToLowerInvariant()).Append(")\n");
        }
    }

    /// <summary>The call's own heading, or its most telling argument.</summary>
    private static string? Describe(ToolUsePart tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Title))
            return OneLine(tool.Title);

        if (tool.Arguments.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "description", "filePath", "path", "command", "pattern", "url", "query" })
        {
            if (tool.Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } argument)
            {
                return OneLine(argument);
            }
        }

        return null;
    }

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 160 ? line : string.Concat(line.AsSpan(0, 159), "…");
    }

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);
}
