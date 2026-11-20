using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.TerminalSessions;

/// <summary>
/// A Claude Code session started in a terminal, brought into Fleet. <c>fleet mcp</c> reports it when it starts and when it
/// ends; the session is added as a Claude Code session (its resume token is Claude Code's session id, so a prompt from Fleet
/// continues the same conversation), and its turns are imported from the transcript at both moments. A session Fleet
/// started itself is never added, and one already added is never added twice.
/// </summary>
public sealed partial class TerminalSessionService(
    TerminalSessionsFeature feature,
    ISessionRepository sessions,
    IMessageRepository messages,
    IProjectRepository projects,
    WorkspaceService workspaces,
    InstanceService instances,
    RepositoryService repositories,
    ITerminalTranscripts transcripts,
    TerminalSessionPresence presence,
    IEventBroadcaster broadcaster,
    SessionActivityWriteService activity,
    IUserContext user,
    ILogger<TerminalSessionService> logger) : ITerminalSessions
{
    private const string HarnessType = "claude-code";

    /// <summary>Raised on the <c>sessions</c> topic when a terminal opens a session or lets go of it.</summary>
    public const string SessionTerminalEventType = "session_terminal";

    // One report at a time: the start and the end of a short session can arrive together.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <inheritdoc />
    public async Task<TerminalSessionAnswer> ReportAsync(TerminalSessionReport report, CancellationToken ct)
    {
        if (report.Event is not (TerminalSessions.Started or TerminalSessions.Heartbeat or TerminalSessions.Ended or TerminalSessions.Changed)
            || string.IsNullOrWhiteSpace(report.SessionId)
            || string.IsNullOrWhiteSpace(report.Folder)
            || !Path.IsPathRooted(report.Folder))
        {
            return new TerminalSessionAnswer("invalid", null);
        }

        if (!await feature.IsEnabledAsync().ConfigureAwait(false))
            return new TerminalSessionAnswer("off", null);

        var claudeSessionId = report.SessionId.Trim();
        var folder = WorkspaceRootService.CanonicalizePath(report.Folder.Trim());

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await sessions.GetByResumeTokenAsync(claudeSessionId).ConfigureAwait(false);
            if (existing is not null && existing.SpawnKind != SpawnKinds.Terminal)
                return new TerminalSessionAnswer("fleet-session", existing.Id);

            // Still open: its new turns show as they are written. (Fleet restarted since it started, or never heard of it: it's new.)
            if (report.Event is TerminalSessions.Heartbeat or TerminalSessions.Changed && existing is not null)
            {
                if (report.Event == TerminalSessions.Heartbeat)
                    await TrackAsync(report, claudeSessionId, folder, existing, ct).ConfigureAwait(false);
                if (presence.IsOpen(claudeSessionId) && !IsUnchanged(claudeSessionId, folder, report.TranscriptPath))
                {
                    var latest = await transcripts.ReadAsync(claudeSessionId, folder, report.TranscriptPath, ct).ConfigureAwait(false);
                    await ImportAsync(existing, latest, live: true, ct).ConfigureAwait(false);
                }

                return new TerminalSessionAnswer("present", existing.Id);
            }

            if (report.Event == TerminalSessions.Changed)
                return new TerminalSessionAnswer("unknown-session", null);

            var transcript = await transcripts.ReadAsync(claudeSessionId, folder, report.TranscriptPath, ct).ConfigureAwait(false);
            if (transcript is { Unknown.Count: > 0 })
                LogUnknownEntries(string.Join(", ", transcript.Unknown));

            if (existing is not null)
            {
                // Nobody typed anything: there's no transcript and no turns, so there is nothing to show.
                if (report.Event == TerminalSessions.Ended && transcript is null && !await messages.HasMessagesAsync(existing.Id).ConfigureAwait(false))
                {
                    presence.Remove(claudeSessionId);
                    await sessions.DeleteAsync(existing.Id).ConfigureAwait(false);
                    await broadcaster.BroadcastAsync("sessions", "session_deleted",
                        JsonSerializer.SerializeToElement(new SessionDeletedOutboxPayload(existing.Id), ApplicationJsonContext.Default.SessionDeletedOutboxPayload),
                        existing.UserId, ct).ConfigureAwait(false);
                    return new TerminalSessionAnswer("removed", null);
                }

                await ImportAsync(existing, transcript, live: true, ct).ConfigureAwait(false);
                await TrackAsync(report, claudeSessionId, folder, existing, ct).ConfigureAwait(false);
                return new TerminalSessionAnswer("updated", existing.Id);
            }

            if (!await InKnownRepositoryAsync(folder, ct).ConfigureAwait(false))
                return new TerminalSessionAnswer("unknown-folder", null);

            var session = await AddSessionAsync(claudeSessionId, folder, transcript, ct).ConfigureAwait(false);
            if (session is null)
                return new TerminalSessionAnswer("invalid", null);

            await ImportAsync(session, transcript, live: false, ct).ConfigureAwait(false);
            await TrackAsync(report, claudeSessionId, folder, session, ct).ConfigureAwait(false);
            return new TerminalSessionAnswer("added", session.Id);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Counts the session as open while it's reporting in, and not once it has said it ended.</summary>
    private async Task TrackAsync(TerminalSessionReport report, string claudeSessionId, string folder, Session session, CancellationToken ct)
    {
        var was = presence.IsOpen(claudeSessionId);
        if (report.Event == TerminalSessions.Ended)
            presence.Remove(claudeSessionId);
        else
            presence.Touch(claudeSessionId, folder, report.TranscriptPath, user.UserId);

        if (was != presence.IsOpen(claudeSessionId) || report.Event == TerminalSessions.Ended)
            await AnnounceAsync(session, presence.IsOpen(claudeSessionId), ct).ConfigureAwait(false);
    }

    /// <summary>Tells the session list that a terminal now has the session open, or has let go of it.</summary>
    internal Task AnnounceAsync(Session session, bool inTerminal, CancellationToken ct)
        => broadcaster.BroadcastAsync("sessions", SessionTerminalEventType,
            JsonSerializer.SerializeToElement(new SessionTerminalOutboxPayload(session.Id, inTerminal), ApplicationJsonContext.Default.SessionTerminalOutboxPayload),
            session.UserId, ct);

    private async Task<Session?> AddSessionAsync(string claudeSessionId, string folder, TerminalTranscript? transcript, CancellationToken ct)
    {
        var workspace = await workspaces.CreateWorkspaceAsync(folder, "existing").ConfigureAwait(false);
        if (workspace.IsFailure)
        {
            LogNotAdded(claudeSessionId, workspace.Error.Description);
            return null;
        }

        var instanceId = $"claude-code-terminal-{Guid.NewGuid():N}";
        var instance = await instances.RegisterInstanceAsync(instanceId, port: 0, pid: null, directory: folder, url: string.Empty).ConfigureAwait(false);
        if (instance.IsFailure)
        {
            LogNotAdded(claudeSessionId, instance.Error.Description);
            return null;
        }

        // Nothing runs for it in Fleet: it wakes, resuming the conversation, when a prompt is sent to it.
        var stoppedAt = DateTime.UtcNow.ToString("O");
        await instances.UpdateInstanceStatusAsync(instanceId, "stopped", stoppedAt).ConfigureAwait(false);

        var scratch = await projects.GetScratchProjectAsync().ConfigureAwait(false);
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = workspace.Value.Id,
            InstanceId = instanceId,
            ProjectId = scratch?.Id,
            OpencodeSessionId = instanceId,
            Title = TitleOf(transcript, folder),
            Status = "stopped",
            LifecycleStatus = "stopped",
            ActivityStatus = "idle",
            StoppedAt = stoppedAt,
            Directory = folder,
            CreatedAt = (transcript is { Messages.Count: > 0 } ? transcript.Messages[0].Timestamp : DateTimeOffset.UtcNow).ToString("O"),
            HarnessType = HarnessType,
            HarnessResumeToken = claudeSessionId,
            UserId = user.UserId,
            SpawnKind = SpawnKinds.Terminal,
        };

        await sessions.InsertAsync(session).ConfigureAwait(false);
        await broadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new SessionCreatedOutboxPayload
            {
                SessionId = session.Id,
                InstanceId = session.InstanceId,
                WorkspaceId = session.WorkspaceId,
                Title = session.Title,
                ProjectId = session.ProjectId,
                SpawnKind = session.SpawnKind,
            }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
            session.UserId, ct).ConfigureAwait(false);

        LogAdded(session.Id, claudeSessionId);
        return session;
    }

    /// <summary>Whether the transcript is as it was when it was last read (so there is nothing new to show).</summary>
    private bool IsUnchanged(string claudeSessionId, string folder, string? path)
    {
        var file = transcripts.FileOf(claudeSessionId, folder, path);
        if (file is null)
            return false;

        try
        {
            var info = new FileInfo(file);
            return presence.IsUnchanged(claudeSessionId, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the transcript's turns that the session doesn't have yet or has changed (a reply being written grows). A
    /// message keeps its id from one read to the next, so a turn is never saved twice. Entries Fleet's own turns wrote
    /// (<see cref="IMessageRepository.GetClaimedTranscriptEntriesAsync"/>) are in the transcript too and are left out.
    /// With <paramref name="live"/> the conversation open in Fleet is told, event by event, as for any turn.
    /// </summary>
    private async Task ImportAsync(Session session, TerminalTranscript? transcript, bool live, CancellationToken ct)
    {
        if (transcript is null)
            return;

        if (transcript.Title is { } title && !string.Equals(title, session.Title, StringComparison.Ordinal))
        {
            await sessions.UpdateTitleAsync(session.Id, title).ConfigureAwait(false);
            session.Title = title;
        }

        var claimed = await messages.GetClaimedTranscriptEntriesAsync(session.Id).ConfigureAwait(false);
        var held = (await messages.GetBySessionAsync(session.Id, int.MaxValue, null).ConfigureAwait(false))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().PartsJson, StringComparer.Ordinal);

        var changed = new List<(HarnessMessage Message, PersistedMessage Persisted)>();
        foreach (var message in transcript.Messages)
        {
            if (transcript.Entries.TryGetValue(message.Id, out var entries) && entries.Any(claimed.Contains))
                continue;

            var persisted = MessagePersistenceService.ToPersistedMessage(session.Id, message);
            if (!held.TryGetValue(message.Id, out var before) || before != persisted.PartsJson)
                changed.Add((message, persisted));
        }

        if (changed.Count == 0)
            return;

        if (!live)
        {
            await messages.UpsertBatchAsync([.. changed.Select(c => c.Persisted)]).ConfigureAwait(false);
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var outbox = changed
                .SelectMany(c => transcripts.EventsOf(session.Id, c.Message))
                .Select(e => new OutboxMessage
                {
                    Topic = $"session:{session.Id}",
                    Type = e.Type,
                    Payload = e.Payload,
                    UserId = session.UserId,
                    CreatedAt = now,
                    AvailableAt = now,
                })
                .ToList();
            await activity.WriteAsync(new SessionActivityWriteRequest { MessageBatchToUpsert = [.. changed.Select(c => c.Persisted)], OutboxMessages = outbox }, ct)
                .ConfigureAwait(false);
        }

        LogImported(session.Id, changed.Count);
    }

    private static string TitleOf(TerminalTranscript? transcript, string folder)
    {
        if ((transcript?.Title ?? transcript?.FirstPrompt) is { } named)
            return named;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? "Claude Code session" : $"Claude Code in {name}";
    }

    /// <summary>
    /// The folder is in a repository Fleet knows: one it found in its workspace roots (or a folder inside it), or a worktree
    /// of one, wherever the worktree is.
    /// </summary>
    private async Task<bool> InKnownRepositoryAsync(string folder, CancellationToken ct)
    {
        var known = await repositories.ScanRepositoriesAsync(ct).ConfigureAwait(false);
        if (known.Any(repository => IsInside(folder, repository.Path)))
            return true;

        string common;
        try
        {
            common = (await GitCommand.RunAsync(folder, TimeSpan.FromSeconds(5), "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is GitCommandException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }

        if (common.Length == 0 || !string.Equals(Path.GetFileName(common.TrimEnd('/', '\\')), ".git", StringComparison.OrdinalIgnoreCase))
            return false;

        var main = Path.GetDirectoryName(WorkspaceRootService.CanonicalizePath(common.TrimEnd('/', '\\')));
        return main is not null && known.Any(repository => IsInside(main, repository.Path));
    }

    private static bool IsInside(string folder, string root)
    {
        var canonicalRoot = WorkspaceRootService.CanonicalizePath(root);
        if (string.Equals(folder, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar;
        return folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal session {ClaudeSessionId} added as session {SessionId}")]
    private partial void LogAdded(string sessionId, string claudeSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Count} turns of terminal session {SessionId}")]
    private partial void LogImported(string sessionId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal session {ClaudeSessionId} wasn't added: {Reason}")]
    private partial void LogNotAdded(string claudeSessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A Claude Code transcript had entries Fleet doesn't read yet: {Unknown}")]
    private partial void LogUnknownEntries(string unknown);
}
