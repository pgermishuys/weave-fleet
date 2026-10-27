using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>
/// Applies what a dreaming model proposed, with the safety rules in code: the model's say-so changes nothing by itself.
/// A change needs evidence from two sessions (or a user's correction it quotes), never touches the user's own notes,
/// and leaves a note alone that changed since the dream began. Each proposal becomes one <see cref="DreamChange"/> with what
/// Undo needs to put it back (<see cref="UndoAsync"/>).
/// </summary>
public sealed class DreamApplier(IMemoryStore store, TimeProvider time)
{
    public const string UserNoteReason = "You wrote this note; dreaming never changes your notes.";
    public const string ChangedReason = "This note changed while Fleet was dreaming, so Fleet left it as is.";
    public const string OneSessionReason = "Seen in one session only.";
    public const string AlreadyANoteReason = "It's already a note.";
    public const string AlreadyKeptReason = "It's already kept for good.";
    public static readonly string KeepSessionsReason = $"Relied on in fewer than {DreamRules.KeepForGoodSessions} sessions.";
    public const string UndoRefused = "This note changed since dreaming changed it; edit it instead.";

    public async Task<List<DreamChange>> ApplyAsync(string userId, Dream dream, IReadOnlyList<DreamProposal> proposals, CancellationToken ct = default)
    {
        var changes = new List<DreamChange>();
        foreach (var proposal in proposals)
            changes.Add(await ApplyOneAsync(userId, dream, proposal, ct).ConfigureAwait(false));
        return changes;
    }

    private async Task<DreamChange> ApplyOneAsync(string userId, Dream dream, DreamProposal p, CancellationToken ct)
    {
        var cited = p.Sessions
            .Select(label => dream.ReadSessions.FirstOrDefault(s => string.Equals(s.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OfType<DreamSession>()
            .DistinctBy(s => s.Id)
            .ToList();
        var change = new DreamChange
        {
            Why = p.Why?.Trim() ?? "",
            Quote = string.IsNullOrWhiteSpace(p.Quote) ? null : p.Quote.Trim(),
            Text = string.IsNullOrWhiteSpace(p.Text) ? null : p.Text.Trim(),
            SessionIds = [.. cited.Select(s => s.Id)],
        };

        var notes = await store.ListForAsync(userId, dream.Repository, ct).ConfigureAwait(false);
        var action = p.Action?.Trim().ToLowerInvariant();
        return action switch
        {
            "add" => await AddAsync(userId, dream, p, change, cited, notes, ct).ConfigureAwait(false),
            "rewrite" => await RewriteAsync(userId, dream, p, change, cited, notes, ct).ConfigureAwait(false),
            "retire" => await RetireAsync(userId, dream, p, change, cited, notes, ct).ConfigureAwait(false),
            "keep" => await KeepAsync(userId, dream, p, change, cited, notes, ct).ConfigureAwait(false),
            "merge" => await MergeAsync(userId, dream, p, change, notes, ct).ConfigureAwait(false),
            _ => Skip(change, $"Unknown action \"{p.Action}\"."),
        };
    }

    private async Task<DreamChange> AddAsync(string userId, Dream dream, DreamProposal p, DreamChange change, List<DreamSession> cited, IReadOnlyList<MemoryNote> notes, CancellationToken ct)
    {
        MemoryList list;
        switch (p.List?.Trim().ToLowerInvariant())
        {
            case "repository": list = MemoryList.Repository; break;
            case "machine": list = MemoryList.Machine; break;
            default: return Skip(change, $"Unknown list \"{p.List}\".");
        }

        change.List = DreamRules.ListName(list);
        if (TextProblem(change.Text) is { } problem)
            return Skip(change, problem);
        if (!HasEvidence(p, cited))
            return Skip(change, OneSessionReason);

        var days = await store.ListDaysUsedAsync(userId, dream.Repository, ct).ConfigureAwait(false);
        var inList = notes.Where(n => n.List == list && (list == MemoryList.Machine || n.Repository == dream.Repository)).ToList();
        if (inList.Any(n => string.Equals(n.Text, change.Text, StringComparison.OrdinalIgnoreCase)))
            return Skip(change, AlreadyANoteReason);
        var active = inList.Count(n => !AgentMemory.IsExpired(n, days.For(n), time.LocalTimeZone));
        if (active >= AgentMemory.MaxNotes(list))
            return Left(change, $"The {change.List} list is full ({active} notes), so Fleet left it as is.", []);

        var now = time.GetUtcNow();
        var note = new MemoryNote(
            DreamRules.NewId(), list, change.Text!, MemoryKinds.Learned,
            list == MemoryList.Repository ? dream.Repository : null,
            dream.SessionId, "Dreaming", now, now, AgentMemory.LearnedLifetimeDays);
        await store.SaveAsync(userId, note, ct).ConfigureAwait(false);
        change.Kind = DreamRules.ChangeKinds.Added;
        change.NoteId = note.Id;
        change.WroteHash = DreamRules.HashOf(note);
        return change;
    }

    private async Task<DreamChange> RewriteAsync(string userId, Dream dream, DreamProposal p, DreamChange change, List<DreamSession> cited, IReadOnlyList<MemoryNote> notes, CancellationToken ct)
    {
        if (dream.Notes.FirstOrDefault(n => n.Id == p.Note?.Trim()) is not { } snapshot)
            return Skip(change, $"There's no note \"{p.Note}\".");

        change.NoteId = snapshot.Id;
        change.List = snapshot.List;
        if (TextProblem(change.Text) is { } problem)
            return Skip(change, problem);
        if (!HasEvidence(p, cited))
            return Skip(change, OneSessionReason);

        var current = notes.FirstOrDefault(n => n.Id == snapshot.Id);
        var replaced = current is null ? [] : new List<MemoryNote> { current };
        if (snapshot.Kind != MemoryKinds.Learned)
            return Left(change, UserNoteReason, replaced);
        if (current is null || DreamRules.HashOf(current) != snapshot.Hash)
            return Left(change, ChangedReason, replaced);

        var written = current with { Text = change.Text!, Updated = time.GetUtcNow() };
        await store.SaveAsync(userId, written, ct).ConfigureAwait(false);
        change.Kind = DreamRules.ChangeKinds.Rewritten;
        change.Replaced = replaced;
        change.WroteHash = DreamRules.HashOf(written);
        return change;
    }

    private async Task<DreamChange> RetireAsync(string userId, Dream dream, DreamProposal p, DreamChange change, List<DreamSession> cited, IReadOnlyList<MemoryNote> notes, CancellationToken ct)
    {
        if (dream.Notes.FirstOrDefault(n => n.Id == p.Note?.Trim()) is not { } snapshot)
            return Skip(change, $"There's no note \"{p.Note}\".");

        change.NoteId = snapshot.Id;
        change.List = snapshot.List;
        change.Text = null;
        if (!HasEvidence(p, cited))
            return Skip(change, OneSessionReason);

        var current = notes.FirstOrDefault(n => n.Id == snapshot.Id);
        var replaced = current is null ? [] : new List<MemoryNote> { current };
        if (snapshot.Kind != MemoryKinds.Learned)
            return Left(change, UserNoteReason, replaced);
        if (current is null || DreamRules.HashOf(current) != snapshot.Hash)
            return Left(change, ChangedReason, replaced);

        await store.DeleteAsync(userId, [current.Id], ct).ConfigureAwait(false);
        change.Kind = DreamRules.ChangeKinds.Retired;
        change.Text = current.Text;
        change.Replaced = replaced;
        return change;
    }

    /// <summary>
    /// Makes a learned note permanent (no lifetime) when 3+ sessions relied on it. Only the lifetime changes (<c>Updated</c> stays),
    /// so Undo can put the note back exactly and nothing else about it moves.
    /// </summary>
    private async Task<DreamChange> KeepAsync(string userId, Dream dream, DreamProposal p, DreamChange change, List<DreamSession> cited, IReadOnlyList<MemoryNote> notes, CancellationToken ct)
    {
        if (dream.Notes.FirstOrDefault(n => n.Id == p.Note?.Trim()) is not { } snapshot)
            return Skip(change, $"There's no note \"{p.Note}\".");

        change.NoteId = snapshot.Id;
        change.List = snapshot.List;
        var current = notes.FirstOrDefault(n => n.Id == snapshot.Id);
        var replaced = current is null ? [] : new List<MemoryNote> { current };
        change.Text = current?.Text ?? snapshot.Text;
        if (snapshot.Kind != MemoryKinds.Learned)
            return Left(change, UserNoteReason, replaced);
        if (current is not null && current.Lifetime is null)
            return Skip(change, AlreadyKeptReason);
        if (cited.Count < DreamRules.KeepForGoodSessions)
            return Skip(change, KeepSessionsReason);
        if (current is null || DreamRules.HashOf(current) != snapshot.Hash)
            return Left(change, ChangedReason, replaced);

        var written = current with { Lifetime = null };
        await store.SaveAsync(userId, written, ct).ConfigureAwait(false);
        change.Kind = DreamRules.ChangeKinds.Kept;
        change.Replaced = replaced;
        change.WroteHash = DreamRules.HashOf(written);
        return change;
    }

    private async Task<DreamChange> MergeAsync(string userId, Dream dream, DreamProposal p, DreamChange change, IReadOnlyList<MemoryNote> notes, CancellationToken ct)
    {
        var removeIds = p.Remove.Select(id => id.Trim()).Where(id => id != p.Keep?.Trim()).Distinct().ToList();
        var snapshots = removeIds.Select(id => dream.Notes.FirstOrDefault(n => n.Id == id)).ToList();
        if (dream.Notes.FirstOrDefault(n => n.Id == p.Keep?.Trim()) is not { } keep)
            return Skip(change, $"There's no note \"{p.Keep}\".");
        if (removeIds.Count == 0)
            return Skip(change, "Nothing to merge into the note.");
        if (snapshots.FindIndex(s => s is null) is var unknown and >= 0)
            return Skip(change, $"There's no note \"{removeIds[unknown]}\".");

        change.NoteId = keep.Id;
        change.List = keep.List;
        var newText = string.IsNullOrWhiteSpace(p.Text) ? null : p.Text.Trim();
        if (newText is not null && TextProblem(newText) is { } problem)
            return Skip(change, problem);
        var keepCurrent = notes.FirstOrDefault(n => n.Id == keep.Id);
        var removed = removeIds.Select(id => notes.FirstOrDefault(n => n.Id == id)).OfType<MemoryNote>().ToList();
        var asTheyAre = keepCurrent is null ? removed : [keepCurrent, .. removed];
        change.Text = keepCurrent?.Text ?? keep.Text;

        if (snapshots.Any(s => s!.Kind != MemoryKinds.Learned))
            return Left(change, UserNoteReason, asTheyAre);
        if (keepCurrent is null || DreamRules.HashOf(keepCurrent) != keep.Hash
            || removed.Count != removeIds.Count
            || snapshots.Any(s => DreamRules.HashOf(removed.First(n => n.Id == s!.Id)) != s!.Hash))
            return Left(change, ChangedReason, asTheyAre);

        await store.DeleteAsync(userId, removeIds, ct).ConfigureAwait(false);
        change.Replaced = removed;
        if (keep.Kind == MemoryKinds.Learned && newText is not null && newText != keepCurrent.Text)
        {
            var written = keepCurrent with { Text = newText, Updated = time.GetUtcNow() };
            await store.SaveAsync(userId, written, ct).ConfigureAwait(false);
            change.Replaced = [keepCurrent, .. removed];
            change.Text = newText;
            change.WroteHash = DreamRules.HashOf(written);
        }

        change.Kind = DreamRules.ChangeKinds.Merged;
        return change;
    }

    /// <summary>Two distinct sessions saw it, or the user corrected it and the quote is in what they wrote in a cited session.</summary>
    private static bool HasEvidence(DreamProposal p, List<DreamSession> cited)
    {
        if (cited.Count >= 2)
            return true;
        if (!p.Corrected || string.IsNullOrWhiteSpace(p.Quote))
            return false;
        var quote = Normalize(p.Quote);
        return quote.Length > 0 && cited.Any(s => s.Prompts.Any(prompt => Normalize(prompt).Contains(quote, StringComparison.Ordinal)));
    }

    private static string Normalize(string text) => string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? TextProblem(string? text)
        => string.IsNullOrWhiteSpace(text) ? "The change has no text."
            : text.Length > AgentMemory.MaxNoteLength ? $"The text is longer than {AgentMemory.MaxNoteLength} characters."
            : null;

    private static DreamChange Skip(DreamChange change, string reason)
    {
        change.Kind = DreamRules.ChangeKinds.Skipped;
        change.Reason = reason;
        return change;
    }

    private static DreamChange Left(DreamChange change, string reason, List<MemoryNote> asTheyAre)
    {
        change.Kind = DreamRules.ChangeKinds.Left;
        change.Reason = reason;
        change.Replaced = asTheyAre;
        return change;
    }

    /// <summary>
    /// Puts back what a change did. Refuses when the note the dream wrote has changed since (or, for a removed note, when
    /// another now has its id): the user's edit wins. Ignores the list's cap. Returns the refusal, or <see langword="null"/>.
    /// </summary>
    public async Task<FleetError?> UndoAsync(string userId, Dream dream, string changeId, CancellationToken ct = default)
    {
        if (dream.Changes.FirstOrDefault(c => c.Id == changeId) is not { } change)
            return FleetError.NotFoundFor("Change", changeId);
        if (change.Undone)
            return FleetError.ValidationError("Dream.Undo", "This change was already undone.");
        if (change.Kind is not (DreamRules.ChangeKinds.Added or DreamRules.ChangeKinds.Rewritten or DreamRules.ChangeKinds.Merged or DreamRules.ChangeKinds.Retired or DreamRules.ChangeKinds.Kept))
            return FleetError.ValidationError("Dream.Undo", "Only a change Fleet made can be undone.");

        var refused = FleetError.ValidationError("Dream.Undo", UndoRefused);
        var id = change.NoteId ?? "";
        switch (change.Kind)
        {
            case DreamRules.ChangeKinds.Added:
                if (!await UnchangedAsync(userId, id, change.WroteHash, ct).ConfigureAwait(false))
                    return refused;
                await store.DeleteAsync(userId, [id], ct).ConfigureAwait(false);
                break;
            case DreamRules.ChangeKinds.Rewritten:
                if (!await UnchangedAsync(userId, id, change.WroteHash, ct).ConfigureAwait(false) || change.Replaced.Count == 0)
                    return refused;
                await store.SaveAsync(userId, change.Replaced[0], ct).ConfigureAwait(false);
                break;
            case DreamRules.ChangeKinds.Kept:
                // Someone giving the note a lifetime again leaves nothing to undo.
                if (!await UnchangedAsync(userId, id, change.WroteHash, ct).ConfigureAwait(false) || change.Replaced.Count == 0
                    || await store.FindAsync(userId, id, ct).ConfigureAwait(false) is not { Lifetime: null })
                    return refused;
                await store.SaveAsync(userId, change.Replaced[0], ct).ConfigureAwait(false);
                break;
            case DreamRules.ChangeKinds.Retired:
                if (change.Replaced.Count == 0 || await store.FindAsync(userId, id, ct).ConfigureAwait(false) is not null)
                    return refused;
                await store.SaveAsync(userId, change.Replaced[0], ct).ConfigureAwait(false);
                break;
            default:
                var keepOld = change.Replaced.FirstOrDefault(n => n.Id == id);
                var removed = change.Replaced.Where(n => n.Id != id).ToList();
                foreach (var note in removed)
                {
                    if (await store.FindAsync(userId, note.Id, ct).ConfigureAwait(false) is not null)
                        return refused;
                }

                if (keepOld is not null && !await UnchangedAsync(userId, id, change.WroteHash, ct).ConfigureAwait(false))
                    return refused;
                foreach (var note in removed)
                    await store.SaveAsync(userId, note, ct).ConfigureAwait(false);
                if (keepOld is not null)
                    await store.SaveAsync(userId, keepOld, ct).ConfigureAwait(false);
                break;
        }

        change.Undone = true;
        return null;
    }

    private async Task<bool> UnchangedAsync(string userId, string id, string? hash, CancellationToken ct)
        => hash is not null && await store.FindAsync(userId, id, ct).ConfigureAwait(false) is { } note && DreamRules.HashOf(note) == hash;
}
