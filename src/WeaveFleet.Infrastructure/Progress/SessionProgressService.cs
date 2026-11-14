using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Progress;

/// <summary>
/// Applies the events <see cref="SessionProgressObserver"/> queues, stores each session's progress, and
/// pushes changes: the row summary on the "sessions" topic and the full detail on the session's own topic.
/// </summary>
internal sealed partial class SessionProgressService(
    IServiceScopeFactory scopeFactory,
    SessionProgressObserver observer,
    IEventBroadcaster broadcaster,
    ILogger<SessionProgressService> logger) : BackgroundService
{
    /// <summary>How many files <see cref="_lastSeen"/> remembers; the least recently seen is forgotten.</summary>
    internal const int MaxRememberedFiles = 64;

    /// <summary>
    /// What each markdown file a session wrote last held as far as Fleet knows, by session and path relative to its
    /// folder. Events are applied well after the calls that made them, so the file on disk may already hold the next
    /// call's change too; an edit is applied to what the file held before it instead. Only <see cref="ApplyAsync"/>
    /// touches it, one event at a time.
    /// </summary>
    private readonly OrderedDictionary<(string SessionId, string Path), string> _lastSeen = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var observed in observer.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ApplyAsync(observed, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogApplyFailed(observed.SessionId, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Applies one event. Returns the new progress, or <see langword="null"/> when nothing changed.</summary>
    internal async Task<SessionProgress?> ApplyAsync(ObservedProgressEvent observed, CancellationToken ct)
    {
        using var userScope = BackgroundUserContext.BeginScope(observed.UserId);
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>();

        var current = await repository.GetForOwnerAsync(observed.SessionId, observed.UserId, ct).ConfigureAwait(false);
        var next = observed.Event switch
        {
            TodosReported todos => SessionProgressTracker.ApplyTodos(
                current, observed.SessionId, observed.UserId, todos.Payload.Items, observed.At),
            FilesWritten written => await ApplyPlanFilesAsync(
                scope.ServiceProvider, current, observed, written.Payload.Paths, written.Payload.Changes, written.Payload.MessageId, ct).ConfigureAwait(false),
            // A turn ended: read the plans again, to catch ticks made by a shell command or in an editor.
            SessionIdled when current is { Plans.Count: > 0 } => await ApplyPlanFilesAsync(
                scope.ServiceProvider, current, observed, [.. current.Plans.Select(plan => plan.Path)], changes: [], messageId: null, ct).ConfigureAwait(false),
            DelegationCreated created => ApplyDelegation(current, observed, created.Payload.DelegationId, created.Payload.ChildSessionId, created.Payload.Title, created.Payload.Status, created.Payload.Description),
            DelegationUpdated updated => ApplyDelegation(current, observed, updated.Payload.DelegationId, updated.Payload.ChildSessionId, updated.Payload.Title, updated.Payload.Status),
            DelegationCompleted completed => ApplyDelegation(current, observed, completed.Payload.DelegationId, completed.Payload.ChildSessionId, completed.Payload.Title, completed.Payload.Status),
            _ => null,
        };

        if (next is null)
            return null;

        // A subagent just linked to its session may already have progress of its own.
        if (observed.Event is DelegationCreated or DelegationUpdated or DelegationCompleted)
            next = await CopySubagentProgressAsync(scope.ServiceProvider, repository, next, observed, ct).ConfigureAwait(false);

        if (!await repository.UpsertAsync(next, ct).ConfigureAwait(false))
            return null;

        await PushAsync(next, includeSummary: true, ct).ConfigureAwait(false);
        await RollUpToParentAsync(scope.ServiceProvider, repository, next, observed.At, ct).ConfigureAwait(false);
        return next;
    }

    private static SessionProgress? ApplyDelegation(
        SessionProgress? current, ObservedProgressEvent observed, string delegationId, string? childSessionId, string agent, string status,
        string? description = null)
        => SessionProgressTracker.ApplyDelegation(
            current, observed.SessionId, observed.UserId, delegationId, childSessionId, agent, status, observed.At, description);

    /// <summary>Copies the progress of each linked subagent session onto its entry; unchanged entries stay as they are.</summary>
    private static async Task<SessionProgress> CopySubagentProgressAsync(
        IServiceProvider services, ISessionProgressRepository repository, SessionProgress progress, ObservedProgressEvent observed, CancellationToken ct)
    {
        var sessions = services.GetRequiredService<ISessionRepository>();
        foreach (var subagent in progress.Subagents.Where(subagent => subagent.ChildSessionId is not null))
        {
            var childId = subagent.ChildSessionId!;
            var child = await repository.GetForOwnerAsync(childId, progress.UserId, ct).ConfigureAwait(false);
            var childSession = await sessions.GetByIdAsync(childId).ConfigureAwait(false);
            progress = SessionProgressTracker.ApplySubagentProgress(progress, childId, child, childSession?.Title, observed.At) ?? progress;
        }

        return progress;
    }

    /// <summary>When the session is a subagent, copies its progress onto its entry in the parent's progress.</summary>
    private async Task RollUpToParentAsync(
        IServiceProvider services, ISessionProgressRepository repository, SessionProgress child, DateTimeOffset at, CancellationToken ct)
    {
        var session = await services.GetRequiredService<ISessionRepository>().GetByIdAsync(child.SessionId).ConfigureAwait(false);
        if (session?.ParentSessionId is not { Length: > 0 } parentId)
            return;

        var parent = await repository.GetForOwnerAsync(parentId, child.UserId, ct).ConfigureAwait(false);
        var updated = parent is null ? null : SessionProgressTracker.ApplySubagentProgress(parent, child.SessionId, child, session.Title, at);
        if (updated is null || !await repository.UpsertAsync(updated, ct).ConfigureAwait(false))
            return;

        // The parent's own counts didn't change, so its row doesn't need a push.
        await PushAsync(updated, includeSummary: false, ct).ConfigureAwait(false);
    }

    private async Task PushAsync(SessionProgress progress, bool includeSummary, CancellationToken ct)
    {
        if (includeSummary)
        {
            var summary = JsonSerializer.SerializeToElement(
                SessionProgressTracker.ToSummary(progress), InfrastructureJsonContext.Default.SessionProgressSummaryDto);
            await broadcaster.BroadcastAsync("sessions", SessionProgressTracker.SummaryEventType, summary, progress.UserId, ct)
                .ConfigureAwait(false);
        }

        var detail = JsonSerializer.SerializeToElement(
            SessionProgressTracker.ToDto(progress), InfrastructureJsonContext.Default.SessionProgressDto);
        await broadcaster.BroadcastAsync($"session:{progress.SessionId}", SessionProgressTracker.DetailEventType, detail, progress.UserId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies what each markdown file says now, or, for a file the call says what it did to, what that call left in
    /// it: the whole file it wrote, or its edit applied to what the file held before. Otherwise the file is read from
    /// the session's folder. Paths are absolute or relative to the session's folder. Returns <see langword="null"/>
    /// when no plan changed.
    /// </summary>
    private async Task<SessionProgress?> ApplyPlanFilesAsync(
        IServiceProvider services,
        SessionProgress? current,
        ObservedProgressEvent observed,
        IReadOnlyList<string> paths,
        IReadOnlyList<FileChange> changes,
        string? messageId,
        CancellationToken ct)
    {
        var candidates = paths.Where(PlanFileReader.IsMarkdown).ToList();
        if (candidates.Count == 0)
            return null;

        var session = await services.GetRequiredService<ISessionRepository>().GetByIdAsync(observed.SessionId).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(session.Directory))
            return null;

        var progress = current;
        var changed = false;
        foreach (var path in candidates)
        {
            var read = await ReadAsync(observed.SessionId, session.Directory, path, changes.LastOrDefault(change => change.Path == path), ct)
                .ConfigureAwait(false);
            if (read.Status == PlanFileStatus.Refused)
                continue;

            var document = read.Status == PlanFileStatus.Read ? ChecklistPlanParser.Parse(read.Content!) : null;
            var next = SessionProgressTracker.ApplyPlanFile(
                progress, observed.SessionId, observed.UserId, read.RelativePath!, document, messageId, observed.At);
            if (next is null)
                continue;

            progress = next;
            changed = true;
        }

        return changed ? progress : null;
    }

    /// <summary>
    /// What <paramref name="path"/> held right after the call that made <paramref name="change"/>, when that can be
    /// worked out; otherwise what it holds now. Remembers the result for the next change to the file.
    /// </summary>
    private async Task<PlanFileRead> ReadAsync(string sessionId, string directory, string path, FileChange? change, CancellationToken ct)
    {
        var located = PlanFileReader.Locate(directory, path);
        var key = (sessionId, located?.RelativePath ?? string.Empty);
        var before = located is not null && _lastSeen.TryGetValue(key, out var seen) ? seen : null;

        var read = change is not null && ContentAfter(change, before) is { } after
            ? PlanFileReader.Accept(directory, path, after)
            : await PlanFileReader.ReadAsync(directory, path, ct).ConfigureAwait(false);

        if (located is not null)
        {
            _lastSeen.Remove(key);
            if (read.Status == PlanFileStatus.Read)
            {
                _lastSeen.Add(key, read.Content!);
                if (_lastSeen.Count > MaxRememberedFiles)
                    _lastSeen.RemoveAt(0);
            }
        }

        return read;
    }

    /// <summary>
    /// The file a change left: the whole file it wrote, or its edit applied to <paramref name="before"/>. Null when
    /// that can't be known: nothing is remembered, or the edit's text isn't there exactly as often as the edit needs
    /// (once, or at least once to replace all), so what Fleet remembers isn't what the edit saw.
    /// </summary>
    internal static string? ContentAfter(FileChange change, string? before)
    {
        if (change.Content is not null)
            return change.Content;
        if (before is null || string.IsNullOrEmpty(change.OldString) || change.NewString is null)
            return null;

        var at = before.IndexOf(change.OldString, StringComparison.Ordinal);
        if (at < 0)
            return null;
        if (change.ReplaceAll)
            return before.Replace(change.OldString, change.NewString, StringComparison.Ordinal);
        if (before.IndexOf(change.OldString, at + 1, StringComparison.Ordinal) >= 0)
            return null;

        return string.Concat(before.AsSpan(0, at), change.NewString, before.AsSpan(at + change.OldString.Length));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update progress for session {SessionId}.")]
    private partial void LogApplyFailed(string sessionId, Exception ex);
}
