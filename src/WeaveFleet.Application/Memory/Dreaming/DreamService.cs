using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.Sessions.Retention;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>
/// The dreaming sessions that are running: session id to dream id and owner, and for a dream with a token limit (a
/// night's), what its model calls have used so far.
/// </summary>
public sealed class DreamSessions
{
    private readonly ConcurrentDictionary<string, (string DreamId, string UserId)> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Allowance> _allowances = new(StringComparer.Ordinal);

    public void Register(string sessionId, string dreamId, string userId, long? tokenLimit = null)
    {
        _running[sessionId] = (dreamId, userId);
        if (tokenLimit is { } limit)
            _allowances[sessionId] = new Allowance(limit);
    }

    public bool TryGet(string sessionId, out (string DreamId, string UserId) entry) => _running.TryGetValue(sessionId, out entry);

    public void Unregister(string sessionId)
    {
        _running.TryRemove(sessionId, out _);
        _allowances.TryRemove(sessionId, out _);
    }

    public bool IsEmpty => _running.IsEmpty;

    /// <summary>
    /// Counts one model call of the session's. True once, for the call that brings it to its token limit: the call
    /// that should stop it. False for a session with no limit, or one already past it.
    /// </summary>
    public bool Spend(string sessionId, long tokens)
        => tokens > 0 && _allowances.TryGetValue(sessionId, out var allowance) && allowance.Spend(tokens);

    /// <summary>What the session's model calls have used so far, while it's registered with a limit.</summary>
    public long Spent(string sessionId) => _allowances.TryGetValue(sessionId, out var allowance) ? allowance.Used : 0;

    private sealed class Allowance(long limit)
    {
        private long _used;
        private int _reached;

        public long Used => Interlocked.Read(ref _used);

        public bool Spend(long tokens)
            => Interlocked.Add(ref _used, tokens) >= limit && Interlocked.Exchange(ref _reached, 1) == 0;
    }
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

    /// <summary>
    /// One model call of a session's, as its harness reported it (<see cref="Sessions.SessionContextRecorder"/>), on the
    /// relay's pump. A dream with a token limit is stopped by the call that reaches it, so it spends at most that one
    /// call past it; with no dream running this is one check.
    /// </summary>
    public void ObserveCall(string sessionId, ContextCall? call)
    {
        if (call is null || sessions.IsEmpty || !sessions.Spend(sessionId, call.Used) || !sessions.TryGet(sessionId, out var entry))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(entry.UserId);
                await scope.ServiceProvider.GetRequiredService<IDreamStopper>().StopForBudgetAsync(sessionId, entry.DreamId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStopFailed(ex, sessionId);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not finish the dream of session {SessionId}")]
    private partial void LogFinishFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not stop the dream of session {SessionId} at its token limit")]
    private partial void LogStopFailed(Exception ex, string sessionId);
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
    ILogger<DreamService> logger) : IDreamStarter, IDreamStopper
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
            How = AgentMemory.CleanHow(note.How),
        })];

        // In the repository's own folder, not a worktree. On a harness whose permission gate Fleet can make read-only
        // (SessionPermissions gives a dreaming session PermissionPolicy.ReadOnlyIn the repository) it may look around the repository;
        // on any other it reads only the prompt. Fleet applies the answer either way.
        var harnessType = await sessionCreation.ResolveHarnessTypeAsync(request.HarnessType).ConfigureAwait(false);
        var readsRepository = DreamRules.ReadOnlyHarnesses.Contains(harnessType);
        var result = await sessionCreation.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = dream.Repository,
            Title = $"Dreaming · {AgentMemory.RepositoryName(dream.Repository)}",
            InitialPrompt = DreamPrompt.Build(dream.Repository, condensed.Text, condensed.Used.Count, dream.Notes,
                [.. condensed.Used.Select(u => u.Id)], readsRepository),
            HarnessType = harnessType,
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

        running.Register(result.Value.Session.Id, dream.Id, userId, request.TokenLimit);
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
        var stoppedAtLimit = dream is { Status: DreamRules.Statuses.Failed, Error: DreamRules.StoppedAtBudget } && dream.SessionId == sessionId;
        if (dream is null || !(dream.IsActive || retried || stoppedAtLimit))
        {
            running.Unregister(sessionId);
            return;
        }

        var snapshot = await messages.GetSnapshotAsync(sessionId, 20).ConfigureAwait(false);
        var last = snapshot.Messages.LastOrDefault(m => m.Info.Role == "assistant");
        if (last is null && !stoppedAtLimit)
            return; // A new session reads idle before its first turn starts.

        var text = last is null ? "" : string.Join("\n", last.Parts.OfType<TextMessageEventPart>().Select(p => p.Text));
        if (stoppedAtLimit && DreamAnswer.Parse(text) is null)
        {
            // Stopped at the night's budget before it answered: it stays stopped. A call that both reached the limit
            // and answered is applied below, since its tokens are spent either way.
            running.Unregister(sessionId);
            return;
        }
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
    /// The dream's session reached its token limit (<see cref="DreamFinisher.ObserveCall"/>): the dream fails at once,
    /// so the night goes on, and its session is stopped. When the session's turn then ends,
    /// <see cref="FinishAsync"/> still applies an answer the last call gave.
    /// </summary>
    public async Task StopForBudgetAsync(string sessionId, string dreamId)
    {
        var userId = user.UserId;
        var dream = await dreams.FindAsync(userId, dreamId).ConfigureAwait(false);
        if (dream is null || !dream.IsActive || dream.SessionId != sessionId)
            return;

        await FailAsync(userId, dreamId, DreamRules.StoppedAtBudget, dream).ConfigureAwait(false);
        using var scope = scopeFactory.CreateScope();
        using var background = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
        var aborted = await scope.ServiceProvider.GetRequiredService<SessionPrompting>().AbortSessionAsync(sessionId).ConfigureAwait(false);
        if (aborted.IsFailure)
            LogAbortFailed(sessionId, aborted.Error.Description);
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
            var earlier = await dreams.ListAsync(userId, dream.Repository, DreamRules.ListLimit).ConfigureAwait(false);
            var flaggedBefore = earlier.Where(d => d.Id != dream.Id)
                .SelectMany(d => d.Changes)
                .Where(c => c.Kind == DreamRules.ChangeKinds.Flagged && c.NoteId is not null && c.NoteHash is not null)
                .Select(c => (c.NoteId!, c.NoteHash!))
                .ToList();
            dream.Changes = await new DreamApplier(memory, time).ApplyAsync(userId, dream, proposals, flaggedBefore).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dreaming couldn't stop session {SessionId} at its token limit: {Error}")]
    private partial void LogAbortFailed(string sessionId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dreaming couldn't read session {SessionId}")]
    private partial void LogReadFailed(Exception ex, string sessionId);
}

/// <summary>What the dreaming session is asked, with the condensed transcripts and the notes inlined.</summary>
public static class DreamPrompt
{
    /// <param name="sessionIds">Fleet's id for each of S1…Sn, in order: the dream can read one in full with <c>fleet_session_read</c>.</param>
    /// <param name="readsRepository">The session may read the repository (it runs read-only); otherwise it only reads this prompt.</param>
    public static string Build(
        string repository, string transcripts, int sessionCount, IReadOnlyList<DreamNote> notes,
        IReadOnlyList<string>? sessionIds = null, bool readsRepository = false)
    {
        var name = AgentMemory.RepositoryName(repository);
        var text = new StringBuilder();
        text.Append("You are Fleet's dreaming pass for the repository ").Append(name).Append(". Below are condensed transcripts of its last ")
            .Append(sessionCount).Append(" sessions (S1..S").Append(sessionCount).Append(") and its current notes (repository and machine, each `[id] (kind, tested or untested) text`).\n\n");

        if (readsRepository)
        {
            text.Append("Your job is to ADD lessons and to CHECK the notes. Check the untested learned notes first, before the others: ")
                .Append("nobody has verified them, so they're the likeliest to be wrong. For each note, ask whether it is still true: ")
                .Append("does the file, folder, command or flag it names still exist (read the repository; `git log`, `git show` and `git grep` tell what was renamed or removed), ")
                .Append("and do the sessions show it is now wrong, or show a command it calls broken working? ")
                .Append("Also find lessons seen in 2+ sessions, or ones the user corrected.\n")
                .Append("You are read-only, and Fleet refuses anything else: read files with the read, grep and glob tools, and run only commands that read ")
                .Append("(cat, ls, head, tail, wc, grep, rg, find, and `git log|show|diff|status|blame|ls-files|grep`; no redirects or `;`). ")
                .Append("Don't edit files, don't use memory tools. The transcripts below are your overview; when you need the detail of a session, ")
                .Append("read it in full with the `fleet_session_read` tool (sessionId below) - read a few at most. Stay brief: you have a token budget.\n");
            if (sessionIds is { Count: > 0 })
            {
                text.Append("Session ids: ");
                for (var index = 0; index < sessionIds.Count; index++)
                    text.Append(index > 0 ? ", " : "").Append('S').Append(index + 1).Append(" = ").Append(sessionIds[index]);
                text.Append(".\n");
            }
        }
        else
        {
            text.Append("Find lessons seen in 2+ sessions, or ones the user corrected, and check them against the notes, the untested learned notes first; ")
                .Append("a note a later session shows to be wrong can be flagged or retired. ")
                .Append("Don't run commands, don't edit files, don't use any memory tools: only read what's below and answer.\n");
        }

        text.Append("Only `learned` notes may be rewritten, merged away or retired. Notes of kind `added` or `from-you` are the user's and are never changed ")
            .Append("(a learned note that duplicates one is merged into it); if one looks out of date, `flag` it with the evidence and the user decides. ")
            .Append("Every rewrite, retire and flag must cite its evidence: the session labels and the user's or the tool's words as `quote`, and ")
            .Append(readsRepository ? "what you read (the file, the commit and what it showed) as `evidence`" : "what the sessions showed as `evidence`")
            .Append(". A note is one fact in a sentence or two, at most ").Append(AgentMemory.MaxNoteLength).Append(" characters, and says what to do. ")
            .Append("An add, rewrite or merge may carry `how`: the evidence that it holds, in a few words (at most ").Append(AgentMemory.MaxHowLength)
            .Append(" characters), e.g. \"seen in S1 and S4: exit 7 without LEDGER_REGION\" or \"checked: scripts/check.sh exists (git log 3 Nov 2026)\". ")
            .Append("When you confirm an untested learned note holds, answer with a `rewrite` that keeps its text and adds the `how`.\n\n")
            .Append("Answer with ONLY a fenced json block:\n```json\n")
            .Append("{\"changes\":[\n")
            .Append(" {\"action\":\"add\",\"list\":\"repository|machine\",\"text\":\"…\",\"how\":\"…\",\"why\":\"one sentence\",\"sessions\":[\"S1\",\"S4\"],\"quote\":\"short failing command/error or the user's words\",\"corrected\":false},\n")
            .Append(" {\"action\":\"rewrite\",\"note\":\"<id>\",\"text\":\"…\",\"how\":\"…\",\"why\":\"…\",\"sessions\":[],\"quote\":\"…\",\"evidence\":\"…\"},\n")
            .Append(" {\"action\":\"merge\",\"keep\":\"<id>\",\"remove\":[\"<id>\"],\"text\":\"optional new wording for keep (learned keep only)\",\"how\":\"…\",\"why\":\"…\"},\n")
            .Append(" {\"action\":\"retire\",\"note\":\"<id>\",\"why\":\"…\",\"sessions\":[],\"quote\":\"…\",\"evidence\":\"…\"},\n")
            .Append(" {\"action\":\"flag\",\"note\":\"<id of an added or from-you note>\",\"why\":\"why it looks out of date\",\"sessions\":[\"S2\"],\"quote\":\"…\",\"evidence\":\"…\"},\n")
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
            text.Append('[').Append(note.Id).Append("] (").Append(note.Kind).Append(note.Fades ? ", fades" : "");
            if (note.Kind == MemoryKinds.Learned)
                text.Append(note.How is { Length: > 0 } how ? ", tested: " + how : ", untested");
            text.Append(") ").Append(note.Text).Append('\n');
        }

        text.Append(any ? "\n" : "(none)\n\n");
    }
}
