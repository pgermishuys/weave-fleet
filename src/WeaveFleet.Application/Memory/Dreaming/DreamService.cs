using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.Sessions.Retention;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>The dreaming sessions that are running: session id to dream id and owner.</summary>
public sealed class DreamSessions
{
    private readonly ConcurrentDictionary<string, (string DreamId, string UserId)> _running = new(StringComparer.Ordinal);

    public void Register(string sessionId, string dreamId, string userId) => _running[sessionId] = (dreamId, userId);

    public bool TryGet(string sessionId, out (string DreamId, string UserId) entry) => _running.TryGetValue(sessionId, out entry);

    public void Unregister(string sessionId) => _running.TryRemove(sessionId, out _);

    public bool IsEmpty => _running.IsEmpty;
}

/// <summary>
/// Ends a dream when its session's turn ends. <see cref="PromptQueueDispatcher"/> hands it every event the relay
/// translates; with no dream running that costs one dictionary lookup, and only on an idle event.
/// </summary>
public sealed partial class DreamFinisher(DreamSessions sessions, IServiceScopeFactory scopeFactory, ILogger<DreamFinisher> logger)
{
    private readonly ConcurrentDictionary<string, byte> _finishing = new(StringComparer.Ordinal);

    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (domainEvent is not SessionIdled || sessions.IsEmpty || !sessions.TryGet(sessionId, out var entry) || !_finishing.TryAdd(sessionId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(entry.UserId);
                await scope.ServiceProvider.GetRequiredService<DreamService>().FinishAsync(sessionId, entry.DreamId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFinishFailed(ex, sessionId);
            }
            finally
            {
                _finishing.TryRemove(sessionId, out _);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not finish the dream of session {SessionId}")]
    private partial void LogFinishFailed(Exception ex, string sessionId);
}

/// <summary>
/// Starts dreams, runs them (read the repository's recent sessions, start the dreaming session, apply its answer) and
/// undoes their changes. A dream that has been reading or running for over an hour no longer blocks another.
/// </summary>
public sealed partial class DreamService(
    IDreamStore dreams,
    IMemoryStore memory,
    AgentMemoryService memoryService,
    DreamingFeature feature,
    ISessionRepository sessions,
    ISessionMessageProxy messages,
    SessionCreation sessionCreation,
    SessionRetention retention,
    IUserContext user,
    DreamSessions running,
    IEventBroadcaster broadcaster,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<DreamService> logger) : IDreamStarter
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private const string StoppedBeforeAnswer = "The dreaming session stopped before it answered. Open it to see why.";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Task<bool> IsEnabledAsync() => feature.IsEnabledAsync();

    public async Task<IReadOnlyList<DreamView>> ListAsync(string? directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return [];
        var found = await dreams.ListAsync(user.UserId, AgentMemory.RepositoryOf(directory), DreamRules.ListLimit, ct).ConfigureAwait(false);
        return [.. found.Select(DreamView.Of)];
    }

    /// <summary>Records the dream and returns it at once; the reading and the dreaming session carry on in the background.</summary>
    public async Task<Result<DreamView>> StartAsync(StartDreamRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Repository) || !Directory.Exists(request.Repository))
            return FleetError.ValidationError("Dream.Repository", "Name a repository folder that exists.");

        var repository = AgentMemory.RepositoryOf(request.Repository);
        var now = time.GetUtcNow();
        var existing = await dreams.ListAsync(user.UserId, repository, DreamRules.ListLimit, ct).ConfigureAwait(false);
        if (existing.Any(d => d.IsActive && now - d.Started < StaleAfter))
            return FleetError.ValidationError("Dream.Running", "Fleet is already dreaming about this repository.");

        var dream = new Dream { Repository = repository, Started = now, HarnessType = request.HarnessType, RunId = request.RunId, Since = request.Since };
        await dreams.SaveAsync(user.UserId, dream, ct).ConfigureAwait(false);

        var userId = user.UserId;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var background = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
                await scope.ServiceProvider.GetRequiredService<DreamService>().ReadAndStartAsync(dream.Id, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStartFailed(ex, dream.Id);
            }
        }, CancellationToken.None);

        return DreamView.Of(dream);
    }

    /// <summary>Picks and condenses the sessions, stores the notes as they are, and starts the dreaming session in the repository itself.</summary>
    internal async Task ReadAndStartAsync(string dreamId, StartDreamRequest request)
    {
        try
        {
            await ReadAndStartCoreAsync(dreamId, request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(ex, dreamId);
            await FailAsync(user.UserId, dreamId, "Fleet couldn't start the dreaming session.").ConfigureAwait(false);
        }
    }

    private async Task ReadAndStartCoreAsync(string dreamId, StartDreamRequest request)
    {
        var userId = user.UserId;
        var dream = await dreams.FindAsync(userId, dreamId).ConfigureAwait(false);
        if (dream is null)
            return;

        // A night reads what was started since the last one; Dream now, the newest.
        var listed = request.Since is { } since
            ? await sessions.ListCreatedSinceAsync(since.UtcDateTime.ToString("O"), DreamRules.NightSessionsScanned).ConfigureAwait(false)
            : await sessions.ListAsync(300, 0, null, null, null, null).ConfigureAwait(false);
        var candidates = listed
            .Where(s => !s.IsHidden && s.ParentSessionId is null && s.SideOfSessionId is null
                && !s.Tags.Contains(DreamRules.SessionTag) && !string.IsNullOrWhiteSpace(s.Directory)
                && string.Equals(AgentMemory.RepositoryOf(s.Directory), dream.Repository, PathComparison))
            .OrderByDescending(s => s.CreatedAt, StringComparer.Ordinal)
            .Take(DreamRules.SessionsRead)
            .ToList();
        if (candidates.Count == 0)
        {
            await FailAsync(userId, dreamId, request.Since is null ? "No sessions in this repository to read yet." : "No new sessions in this repository to read.").ConfigureAwait(false);
            return;
        }

        var read = new List<TranscriptSession>();
        foreach (var session in candidates)
        {
            try
            {
                var snapshot = await messages.GetSnapshotAsync(session.Id, 100).ConfigureAwait(false);
                read.Add(new TranscriptSession(session.Id, session.Title, session.HarnessType, snapshot.Messages));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogReadFailed(ex, session.Id);
            }
        }

        var condensed = DreamTranscript.Build(read);
        if (condensed.Used.Count == 0)
        {
            await FailAsync(userId, dreamId, "No sessions in this repository to read yet.").ConfigureAwait(false);
            return;
        }

        dream.ReadSessions = [.. condensed.Used.Select((s, index) => new DreamSession
        {
            Label = $"S{index + 1}",
            Id = s.Id,
            Title = s.Title,
            HarnessType = s.HarnessType,
            Prompts = [.. s.Messages.Where(m => m.Info.Role == "user")
                .Select(m => string.Join(' ', m.Parts.OfType<TextMessageEventPart>().Select(p => p.Text)))
                .Where(text => !string.IsNullOrWhiteSpace(text))],
        })];
        dream.Notes = [.. (await memory.ListForAsync(userId, dream.Repository).ConfigureAwait(false)).Select(note => new DreamNote
        {
            Id = note.Id,
            List = DreamRules.ListName(note.List),
            Kind = note.Kind,
            Text = note.Text,
            Hash = DreamRules.HashOf(note),
            Fades = note.Kind == MemoryKinds.Learned && note.Lifetime is not null,
        })];

        // In the repository's own folder, not a worktree: it reads, and Fleet applies.
        var result = await sessionCreation.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = dream.Repository,
            Title = $"Dreaming · {AgentMemory.RepositoryName(dream.Repository)}",
            InitialPrompt = DreamPrompt.Build(dream.Repository, condensed.Text, condensed.Used.Count, dream.Notes),
            HarnessType = request.HarnessType,
            ProviderId = request.ProviderId,
            ModelId = request.ModelId,
            Tags = [DreamRules.SessionTag],
            SourceReference = $"{DreamRules.SourceReferencePrefix}{dream.Id}",
        }).ConfigureAwait(false);
        if (result.IsFailure)
        {
            await FailAsync(userId, dreamId, $"Couldn't start the dreaming session: {result.Error.Description}", dream).ConfigureAwait(false);
            return;
        }

        running.Register(result.Value.Session.Id, dream.Id, userId);
        dream.SessionId = result.Value.Session.Id;
        dream.HarnessType = result.Value.Session.HarnessType;
        dream.Status = DreamRules.Statuses.Running;
        await dreams.SaveAsync(userId, dream).ConfigureAwait(false);
    }

    /// <summary>The dreaming session's turn ended: apply its answer, archive it and tell the windows.</summary>
    internal async Task FinishAsync(string sessionId, string dreamId)
    {
        var userId = user.UserId;
        var dream = await dreams.FindAsync(userId, dreamId).ConfigureAwait(false);
        var retried = dream is { Status: DreamRules.Statuses.Failed, Error: StoppedBeforeAnswer } && dream.SessionId == sessionId;
        if (dream is null || !(dream.IsActive || retried))
        {
            running.Unregister(sessionId);
            return;
        }

        var snapshot = await messages.GetSnapshotAsync(sessionId, 20).ConfigureAwait(false);
        var last = snapshot.Messages.LastOrDefault(m => m.Info.Role == "assistant");
        if (last is null)
            return; // A new session reads idle before its first turn starts.

        var text = string.Join("\n", last.Parts.OfType<TextMessageEventPart>().Select(p => p.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            // The turn failed before the model said anything (a usage limit, say). The dream stops blocking another, but
            // stays registered: if Fleet's retry of the turn answers, that turn's end still applies it.
            if (dream.Status != DreamRules.Statuses.Failed)
                await FailAsync(userId, dreamId, StoppedBeforeAnswer, dream).ConfigureAwait(false);
            return;
        }

        var applied = await CompleteAsync(userId, dream, text).ConfigureAwait(false);
        running.Unregister(sessionId);
        if (applied)
        {
            await retention.ArchiveSessionAsync(sessionId).ConfigureAwait(false);
            await memoryService.RefreshAfterDreamAsync(userId, dream.Repository).ConfigureAwait(false);
        }

        await BroadcastAsync(userId, dream).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the final message's changes to <paramref name="dream"/> and saves it: done, or failed when the message has
    /// no list of changes. Returns whether it was done.
    /// </summary>
    public async Task<bool> CompleteAsync(string userId, Dream dream, string? finalMessage)
    {
        dream.Finished = time.GetUtcNow();
        var proposals = DreamAnswer.Parse(finalMessage);
        if (proposals is null)
        {
            dream.Status = DreamRules.Statuses.Failed;
            dream.Error = "The dreaming session ended without a list of changes.";
        }
        else
        {
            dream.Changes = await new DreamApplier(memory, time).ApplyAsync(userId, dream, proposals).ConfigureAwait(false);
            dream.Status = DreamRules.Statuses.Done;
        }

        await dreams.SaveAsync(userId, dream).ConfigureAwait(false);
        return proposals is not null;
    }

    public async Task<Result<DreamView>> UndoAsync(string dreamId, string changeId, CancellationToken ct = default)
    {
        var dream = await dreams.FindAsync(user.UserId, dreamId, ct).ConfigureAwait(false);
        if (dream is null)
            return FleetError.NotFoundFor("Dream", dreamId);

        if (await new DreamApplier(memory, time).UndoAsync(user.UserId, dream, changeId, ct).ConfigureAwait(false) is { } refused)
            return refused;

        await dreams.SaveAsync(user.UserId, dream, ct).ConfigureAwait(false);
        await memoryService.RefreshAfterDreamAsync(user.UserId, dream.Repository, ct).ConfigureAwait(false);
        return DreamView.Of(dream);
    }

    private async Task FailAsync(string userId, string dreamId, string error, Dream? dream = null)
    {
        dream ??= await dreams.FindAsync(userId, dreamId).ConfigureAwait(false);
        if (dream is null)
            return;
        dream.Status = DreamRules.Statuses.Failed;
        dream.Error = error;
        dream.Finished = time.GetUtcNow();
        await dreams.SaveAsync(userId, dream).ConfigureAwait(false);
        await BroadcastAsync(userId, dream).ConfigureAwait(false);
    }

    private Task BroadcastAsync(string userId, Dream dream)
        => broadcaster.BroadcastAsync(
            "sessions",
            DreamRules.DreamedEventType,
            JsonSerializer.SerializeToElement(DreamView.Of(dream), ApplicationJsonContext.Default.DreamView),
            userId,
            CancellationToken.None);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start dream {DreamId}")]
    private partial void LogStartFailed(Exception ex, string dreamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dreaming couldn't read session {SessionId}")]
    private partial void LogReadFailed(Exception ex, string sessionId);
}

/// <summary>What the dreaming session is asked, with the condensed transcripts and the notes inlined.</summary>
public static class DreamPrompt
{
    public static string Build(string repository, string transcripts, int sessionCount, IReadOnlyList<DreamNote> notes)
    {
        var name = AgentMemory.RepositoryName(repository);
        var text = new StringBuilder();
        text.Append("You are Fleet's dreaming pass for the repository ").Append(name).Append(". Below are condensed transcripts of its last ")
            .Append(sessionCount).Append(" sessions (S1..S").Append(sessionCount).Append(") and its current notes (repository and machine, each `[id] (kind) text`).\n\n")
            .Append("Find lessons seen in 2+ sessions, or ones the user corrected, and check them against the notes. ")
            .Append("Don't run commands, don't edit files, don't use any memory tools: only read what's below and answer.\n")
            .Append("Only `learned` notes may be rewritten, merged away or retired. Notes of kind `added` or `from-you` are the user's and are never changed (a learned note that duplicates one is merged into it). ")
            .Append("A note is one fact in a sentence or two, at most ").Append(AgentMemory.MaxNoteLength).Append(" characters, and says what to do.\n\n")
            .Append("Answer with ONLY a fenced json block:\n```json\n")
            .Append("{\"changes\":[\n")
            .Append(" {\"action\":\"add\",\"list\":\"repository|machine\",\"text\":\"…\",\"why\":\"one sentence\",\"sessions\":[\"S1\",\"S4\"],\"quote\":\"short failing command/error or the user's words\",\"corrected\":false},\n")
            .Append(" {\"action\":\"rewrite\",\"note\":\"<id>\",\"text\":\"…\",\"why\":\"…\",\"sessions\":[],\"quote\":\"…\"},\n")
            .Append(" {\"action\":\"merge\",\"keep\":\"<id>\",\"remove\":[\"<id>\"],\"text\":\"optional new wording for keep (learned keep only)\",\"why\":\"…\"},\n")
            .Append(" {\"action\":\"retire\",\"note\":\"<id>\",\"why\":\"…\",\"sessions\":[],\"quote\":\"…\"},\n")
            .Append(" {\"action\":\"keep\",\"note\":\"<id>\",\"why\":\"…\",\"sessions\":[\"S1\",\"S3\",\"S5\"],\"quote\":\"…\"}\n")
            .Append("]}\n```\nAn empty list is a fine answer. Keep (for good) is for a learned note marked `fades` that ").Append(DreamRules.KeepForGoodSessions)
            .Append("+ sessions relied on and that would otherwise fade; cite those sessions.\n\n");

        Section(text, $"Repository notes ({name})", notes.Where(n => n.List == "repository"));
        Section(text, "Machine notes", notes.Where(n => n.List == "machine"));
        return text.Append("# Sessions\n\n").Append(transcripts).ToString();
    }

    private static void Section(StringBuilder text, string heading, IEnumerable<DreamNote> notes)
    {
        text.Append("# ").Append(heading).Append('\n');
        var any = false;
        foreach (var note in notes)
        {
            any = true;
            text.Append('[').Append(note.Id).Append("] (").Append(note.Kind).Append(note.Fades ? ", fades) " : ") ").Append(note.Text).Append('\n');
        }

        text.Append(any ? "\n" : "(none)\n\n");
    }
}
