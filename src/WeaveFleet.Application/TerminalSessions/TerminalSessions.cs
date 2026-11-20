using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.TerminalSessions;

/// <summary>
/// Claude Code sessions started in a terminal, shown in Fleet (Settings → Labs, for now). Fleet learns of them from
/// <c>fleet mcp</c>, the MCP server Claude Code starts with each session, and reads their turns from the session's
/// transcript. Off, nothing is reported to or kept by Fleet.
/// </summary>
public static class TerminalSessions
{
    /// <summary>The user preference that turns it on.</summary>
    public const string PreferenceKey = "TerminalSessions";

    /// <summary>A session started in a terminal: its MCP server tells Fleet.</summary>
    public const string Started = "started";

    /// <summary>
    /// The terminal session ended: its MCP server's input closed, or it was told to stop. Claude Code usually just kills the
    /// server, so Fleet also takes silence for the end (<see cref="HeartbeatEvery"/>).
    /// </summary>
    public const string Ended = "ended";

    /// <summary>The session is still open: sent every <see cref="HeartbeatEvery"/> while its MCP server runs.</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>
    /// The session's transcript was written to. Fleet's own watcher of the file says so (not <c>fleet mcp</c>), so the
    /// session isn't counted as heard from.
    /// </summary>
    public const string Changed = "changed";

    /// <summary>How often a terminal session's MCP server says it is still there.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long without a heartbeat before Fleet counts the session as ended.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(16);
}

/// <summary>Whether terminal sessions are on for the current user.</summary>
public sealed class TerminalSessionsFeature(IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync() =>
        string.Equals(await preferences.GetAsync(TerminalSessions.PreferenceKey).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What <c>fleet mcp</c> tells the Fleet on its machine.</summary>
/// <param name="Event"><see cref="TerminalSessions.Started"/>, <see cref="TerminalSessions.Heartbeat"/> or <see cref="TerminalSessions.Ended"/>.</param>
/// <param name="SessionId">Claude Code's session id; null when the server wasn't given one (it then names the folder alone).</param>
/// <param name="Folder">The folder Claude Code ran in.</param>
/// <param name="TranscriptPath">Where the terminal's Claude Code keeps the session's transcript, for a Fleet that doesn't share its home folder.</param>
public sealed record TerminalSessionReport(string? Event, string? SessionId, string? Folder, string? TranscriptPath = null);

/// <summary>
/// What Fleet did with a report. <c>Status</c> is <c>added</c> (a session now shows), <c>updated</c> (it already did; its
/// turns were read again), <c>off</c> (the switch is off), <c>unknown-folder</c> (not in a repository Fleet knows),
/// <c>removed</c> (it ended with nothing typed in it, so it no longer shows),
/// <c>fleet-session</c> (Fleet started this session itself) or <c>invalid</c>.
/// </summary>
public sealed record TerminalSessionAnswer(string Status, string? SessionId);

/// <summary>Brings a terminal session into Fleet, and reads its turns again when it ends.</summary>
public interface ITerminalSessions
{
    Task<TerminalSessionAnswer> ReportAsync(TerminalSessionReport report, CancellationToken ct);
}

/// <summary>What a Claude Code session transcript holds, as Fleet's messages.</summary>
/// <param name="Messages">The conversation, oldest first, with ids that are the same on every read of the same entries.</param>
/// <param name="Title">The name the session has (one the user gave it, else Claude Code's); null when it has none.</param>
/// <param name="FirstPrompt">What the user asked first, for a title when nothing names the session.</param>
/// <param name="Folder">The folder the session ran in.</param>
/// <param name="Unknown">The entry types and content blocks the reader didn't know, each once.</param>
public sealed record TerminalTranscript(
    IReadOnlyList<WeaveFleet.Domain.Harnesses.HarnessMessage> Messages,
    string? Title,
    string? FirstPrompt,
    string? Folder,
    IReadOnlyList<string> Unknown)
{
    /// <summary>The uuids of the transcript entries each message was made from, by message id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Entries { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>Reads a terminal session's transcript.</summary>
public interface ITerminalTranscripts
{
    /// <summary>
    /// The transcript of Claude Code session <paramref name="sessionId"/> run in <paramref name="folder"/>, from
    /// <paramref name="path"/> when the session's MCP server named it, otherwise from where Claude Code keeps it. Null
    /// when there is none (a session nobody has typed in yet writes none).
    /// </summary>
    Task<TerminalTranscript?> ReadAsync(string sessionId, string folder, string? path, CancellationToken ct);

    /// <summary>
    /// The events that show <paramref name="message"/> in the open session <paramref name="fleetSessionId"/>: the
    /// message, then each of its parts (the same events the session's live stream sends).
    /// </summary>
    IReadOnlyList<TerminalEvent> EventsOf(string fleetSessionId, WeaveFleet.Domain.Harnesses.HarnessMessage message);

    /// <summary>The file Claude Code writes the session's transcript to, when it exists.</summary>
    string? FileOf(string sessionId, string folder, string? path);
}

/// <summary>An event for the session's live stream: its <paramref name="Type"/> and its JSON <paramref name="Payload"/>.</summary>
public sealed record TerminalEvent(string Type, string Payload);

/// <summary>
/// The terminal sessions whose MCP servers are still reporting in. Claude Code usually kills the server when a session
/// ends, so a session whose heartbeats stop is the one that ended.
/// </summary>
public sealed class TerminalSessionPresence(TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Open> _open = new(StringComparer.Ordinal);

    /// <summary>Raised when a session that wasn't open starts reporting in.</summary>
    public event Action<Open>? Opened;

    /// <summary>Raised when a session isn't open any more.</summary>
    public event Action<string>? Closed;

    /// <summary>A session still open, and what is needed to read it again once it isn't.</summary>
    public sealed record Open(string ClaudeSessionId, string Folder, string? TranscriptPath, string UserId, DateTimeOffset LastSeen);

    /// <summary>Notes that the session is there now.</summary>
    public void Touch(string claudeSessionId, string folder, string? transcriptPath, string userId)
    {
        var open = new Open(claudeSessionId, folder, transcriptPath, userId, time.GetUtcNow());
        var isNew = !_open.ContainsKey(claudeSessionId);
        _open[claudeSessionId] = open;
        if (isNew)
            Opened?.Invoke(open);
    }

    /// <summary>The session is over.</summary>
    public void Remove(string claudeSessionId)
    {
        _stamps.TryRemove(claudeSessionId, out _);
        if (_open.TryRemove(claudeSessionId, out _))
            Closed?.Invoke(claudeSessionId);
    }

    /// <summary>The sessions open now.</summary>
    public IReadOnlyList<Open> OpenSessions() => [.. _open.Values];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stamps = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the transcript is as it was when it was last read. <paramref name="stamp"/> says how it is now (its size and
    /// time); the first call after the session opens, or after <see cref="Forget"/>, is never "as it was".
    /// </summary>
    public bool IsUnchanged(string claudeSessionId, string stamp)
    {
        if (_stamps.TryGetValue(claudeSessionId, out var last) && last == stamp)
            return true;
        _stamps[claudeSessionId] = stamp;
        return false;
    }

    /// <summary>Drops what was noted about the transcript, so the next look reads it.</summary>
    public void Forget(string claudeSessionId) => _stamps.TryRemove(claudeSessionId, out _);

    /// <summary>Whether Fleet has heard from the session's MCP server lately.</summary>
    public bool IsOpen(string claudeSessionId) => _open.ContainsKey(claudeSessionId);

    /// <summary>The sessions not heard from for <paramref name="silence"/>, which are no longer counted as open.</summary>
    public IReadOnlyList<Open> TakeSilent(TimeSpan silence)
    {
        var cutoff = time.GetUtcNow() - silence;
        var silent = new List<Open>();
        foreach (var (id, open) in _open)
        {
            if (open.LastSeen <= cutoff && _open.TryRemove(id, out _))
            {
                silent.Add(open);
                _stamps.TryRemove(id, out _);
                Closed?.Invoke(id);
            }
        }

        return silent;
    }
}
