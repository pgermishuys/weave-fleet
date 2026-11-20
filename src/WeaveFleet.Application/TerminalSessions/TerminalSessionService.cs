using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Entities;
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
    IUserContext user,
    ILogger<TerminalSessionService> logger) : ITerminalSessions
{
    private const string HarnessType = "claude-code";

    // One report at a time: the start and the end of a short session can arrive together.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <inheritdoc />
    public async Task<TerminalSessionAnswer> ReportAsync(TerminalSessionReport report, CancellationToken ct)
    {
        if (report.Event is not (TerminalSessions.Started or TerminalSessions.Heartbeat or TerminalSessions.Ended)
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

            // Still there: nothing to read until it ends. (Fleet restarted since it started, or never heard of it: it's new.)
            if (report.Event == TerminalSessions.Heartbeat && existing is not null)
            {
                presence.Touch(claudeSessionId, folder, report.TranscriptPath, user.UserId);
                return new TerminalSessionAnswer("present", existing.Id);
            }

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

                await ImportAsync(existing, transcript, ct).ConfigureAwait(false);
                Track(report, claudeSessionId, folder);
                return new TerminalSessionAnswer("updated", existing.Id);
            }

            if (!await InKnownRepositoryAsync(folder, ct).ConfigureAwait(false))
                return new TerminalSessionAnswer("unknown-folder", null);

            var session = await AddSessionAsync(claudeSessionId, folder, transcript, ct).ConfigureAwait(false);
            if (session is null)
                return new TerminalSessionAnswer("invalid", null);

            await ImportAsync(session, transcript, ct).ConfigureAwait(false);
            Track(report, claudeSessionId, folder);
            return new TerminalSessionAnswer("added", session.Id);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Counts the session as open while it's reporting in, and not once it has said it ended.</summary>
    private void Track(TerminalSessionReport report, string claudeSessionId, string folder)
    {
        if (report.Event == TerminalSessions.Ended)
            presence.Remove(claudeSessionId);
        else
            presence.Touch(claudeSessionId, folder, report.TranscriptPath, user.UserId);
    }

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

    /// <summary>
    /// Saves the transcript's turns that the session doesn't have yet. A message keeps its id from one read to the next,
    /// so a turn is never saved twice. Turns Fleet itself ran in the session since are in the transcript too, under ids of
    /// their own: nothing from the first of them on is imported again.
    /// </summary>
    private async Task ImportAsync(Session session, TerminalTranscript? transcript, CancellationToken ct)
    {
        if (transcript is null)
            return;

        if (transcript.Title is { } title && !string.Equals(title, session.Title, StringComparison.Ordinal))
        {
            await sessions.UpdateTitleAsync(session.Id, title).ConfigureAwait(false);
            session.Title = title;
        }

        var incoming = transcript.Messages;
        var held = await messages.GetBySessionAsync(session.Id, int.MaxValue, null).ConfigureAwait(false);
        if (held.Count > 0)
        {
            var ids = incoming.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var ownTurns = held.Where(m => !ids.Contains(m.Id)).Select(m => m.Timestamp).Where(t => t.Length > 0).ToList();
            if (ownTurns.Count > 0)
            {
                var firstOwn = ownTurns.Select(t => DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)).Min();
                incoming = [.. incoming.Where(m => m.Timestamp < firstOwn)];
            }
        }

        if (incoming.Count == 0)
            return;

        await messages.UpsertBatchAsync([.. incoming.Select(m => MessagePersistenceService.ToPersistedMessage(session.Id, m))]).ConfigureAwait(false);
        LogImported(session.Id, incoming.Count);
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
