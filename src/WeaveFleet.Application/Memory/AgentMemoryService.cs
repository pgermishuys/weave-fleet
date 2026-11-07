using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory;

/// <summary>A repository with notes, or one the user has sessions in, as Settings lists it.</summary>
public sealed record MemoryRepositoryView(string Path, string Name, int Count);

/// <summary>Whether memory is on, and what there is to show.</summary>
public sealed record MemoryOverview(
    bool Enabled,
    IReadOnlyList<MemoryRepositoryView> Repositories,
    int MachineCount,
    int MaxRepositoryNotes,
    int MaxMachineNotes,
    bool SearchEnabled = false,
    int MaxAlwaysRepositoryNotes = AgentMemory.MaxAlwaysRepositoryNotes,
    int MaxAlwaysMachineNotes = AgentMemory.MaxAlwaysMachineNotes,
    int MaxSearchRepositoryNotes = AgentMemory.MaxSearchRepositoryNotes,
    int MaxSearchMachineNotes = AgentMemory.MaxSearchMachineNotes);

/// <summary>
/// A note as the API shows it. <see cref="List"/> is <c>repository</c> or <c>machine</c>. <see cref="Lifetime"/> is how
/// many days of use the note lasts (none: it never expires), <see cref="DaysLeft"/> how many of them are left, and
/// <see cref="Relearned"/> how many times an agent learned it again after it expired.
/// </summary>
public sealed record MemoryNoteView(
    string Id,
    string List,
    string Text,
    string Kind,
    string? Repository,
    string? SessionId,
    string? SessionTitle,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    int? Lifetime = null,
    int? DaysLeft = null,
    bool Expired = false,
    int Relearned = 0,
    string? How = null,
    bool InSearch = false,
    bool Pinned = false,
    int Found = 0,
    DateTimeOffset? LastFound = null);

/// <summary>
/// What a session in <see cref="Repository"/> reads, and roughly what that costs per request. <see cref="ExpiredNotes"/>
/// are learned notes that had their days: sessions don't read them, and the same lesson learned again brings one back.
/// </summary>
public sealed record MemoryNotesView(
    string? Repository,
    IReadOnlyList<MemoryNoteView> RepositoryNotes,
    IReadOnlyList<MemoryNoteView> MachineNotes,
    int Tokens,
    IReadOnlyList<MemoryNoteView>? ExpiredNotes = null);

/// <summary>
/// What Fleet tells the user's windows when an agent saves a note, so they can offer Undo. <see cref="Previous"/> is the
/// note it replaced, as it was, so Undo can put it back.
/// </summary>
public sealed record MemorySavedPayload(MemoryNoteView Note, string? RepositoryName, MemoryNoteView? Previous);

/// <summary>
/// The note an agent saved, and the one it replaced. <see cref="Relearned"/>: it brought back an expired note that said
/// much the same, which now lasts longer.
/// </summary>
public sealed record MemorySaveOutcome(MemoryNote Note, string? Replaced, bool AlreadyKnown, bool Relearned = false, IReadOnlyList<MemoryNote>? MovedDown = null);

/// <summary>What a search found: the notes (best first) and how many notes it looked through.</summary>
public sealed record MemorySearchOutcome(IReadOnlyList<MemoryNote> Notes, int Searched);

/// <summary>
/// The user's notes: Settings reads and edits them, agents save and forget them (<see cref="AgentMemoryBridge"/>), and
/// every prompt writes what its session reads (<see cref="PrepareSessionAsync"/>). After any change the files every
/// session folder reads are written again, for the sessions that start next. A running session keeps the notes it
/// started with in its instructions, and hears about a change with its next prompt (<see cref="ChangesForAsync"/>).
/// <para>
/// A note an agent learned lasts <see cref="AgentMemory.LearnedLifetimeDays"/> days of use, then sessions stop reading
/// it. Fleet keeps it, hidden: when an agent learns much the same lesson again, the old note comes back with twice the
/// lifetime, so lessons that keep coming back stay and the ones whose cause is gone drop out.
/// </para>
/// </summary>
public sealed partial class AgentMemoryService(
    IMemoryStore store,
    IUserPreferenceRepository preferences,
    IUserContext user,
    TimeProvider? time = null,
    IEventBroadcaster? broadcaster = null,
    ISessionRepository? sessions = null,
    ILogger<AgentMemoryService>? logger = null,
    AgentMemorySessions? told = null)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<bool> IsEnabledAsync() => new AgentMemoryFeature(preferences).IsEnabledAsync();

    /// <summary>Whether memory and "Search older notes" are both on: notes fall into a short list every session reads, and the rest.</summary>
    public Task<bool> IsSearchOnAsync() => new AgentMemorySearchFeature(preferences).IsEnabledAsync();

    public async Task<MemoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var notes = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        var counts = notes
            .Where(note => note.List == MemoryList.Repository && note.Repository is not null)
            .GroupBy(note => note.Repository!, PathComparer)
            .ToDictionary(group => group.Key, group => group.Count(), PathComparer);

        // Repositories the user works in show up too, so a note can be added before any agent saved one.
        if (sessions is not null)
        {
            foreach (var directory in (await sessions.ListAsync(200, 0).ConfigureAwait(false)).Select(session => session.Directory).Distinct(PathComparer))
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    counts.TryAdd(AgentMemory.RepositoryOf(directory), 0);
            }
        }

        var repositories = counts
            .Select(pair => new MemoryRepositoryView(pair.Key, AgentMemory.RepositoryName(pair.Key), pair.Value))
            .OrderByDescending(repository => repository.Count)
            .ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MemoryOverview(
            await IsEnabledAsync().ConfigureAwait(false),
            repositories,
            notes.Count(note => note.List == MemoryList.Machine),
            AgentMemory.MaxRepositoryNotes,
            AgentMemory.MaxMachineNotes,
            await IsSearchOnAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Turns memory on or off. Off keeps the notes but deletes what sessions read, and running sessions are told with
    /// their next prompt not to rely on the notes they started with; sessions started afterwards run without the
    /// memory tools.
    /// </summary>
    public async Task<MemoryOverview> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await preferences.SetAsync(AgentMemory.PreferenceKey, enabled ? "true" : "false").ConfigureAwait(false);
        if (enabled)
            await RefreshContextAsync(user.UserId, Touched.Everything, ct).ConfigureAwait(false);
        else
            await store.ClearContextAsync(user.UserId, ct).ConfigureAwait(false);
        return await GetOverviewAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The notes a session in <paramref name="directory"/> reads: its repository's and the machine's.</summary>
    public async Task<MemoryNotesView> ListAsync(string? directory, CancellationToken ct = default)
    {
        var repository = string.IsNullOrWhiteSpace(directory) ? null : AgentMemory.RepositoryOf(directory);
        var notes = await store.ListForAsync(user.UserId, repository, ct).ConfigureAwait(false);
        var days = await store.ListDaysUsedAsync(user.UserId, repository, ct).ConfigureAwait(false);
        var all = (repository is null ? [] : RepositoryNotes(notes, repository)).Concat(MachineNotes(notes)).ToList();
        var expired = all.Where(note => IsExpired(note, days)).ToList();
        var repositoryNotes = all.Except(expired).Where(note => note.List == MemoryList.Repository).ToList();
        var machineNotes = all.Except(expired).Where(note => note.List == MemoryList.Machine).ToList();

        // Turning "Search older notes" on leaves lists longer than the short one; they're sorted out when first looked at.
        var searchOn = await IsSearchOnAsync().ConfigureAwait(false);
        if (searchOn && repository is not null)
        {
            var fitRepository = await FitAsync(user.UserId, repositoryNotes, MemoryList.Repository, 0, ct).ConfigureAwait(false);
            var fitMachine = await FitAsync(user.UserId, machineNotes, MemoryList.Machine, 0, ct).ConfigureAwait(false);
            if (fitRepository.Moved.Count + fitMachine.Moved.Count > 0)
                await RefreshContextAsync(user.UserId, Touched.MachineList.And(new Touched(Machine: false, repository)), ct).ConfigureAwait(false);
        }

        var tokens = 0;
        if (repository is not null)
        {
            var (repositoryRead, repositoryMore) = MemoryTiers.Choose(repositoryNotes, MemoryList.Repository, searchOn);
            var (machineRead, machineMore) = MemoryTiers.Choose(machineNotes, MemoryList.Machine, searchOn);
            tokens = EstimateTokens(AgentMemoryPrompt.Render(
                repository, repositoryRead, machineRead, true, searchOn ? repositoryMore : null, searchOn ? machineMore : null));
        }

        return new MemoryNotesView(
            repository,
            [.. repositoryNotes.OrderByDescending(note => note.Updated).Select(note => ToView(note, days))],
            [.. machineNotes.OrderByDescending(note => note.Updated).Select(note => ToView(note, days))],
            tokens,
            [.. expired.OrderByDescending(note => note.Updated).Select(note => ToView(note, days))]);
    }

    /// <summary>A note the user writes in Settings.</summary>
    public async Task<Result<MemoryNoteView>> AddAsync(string? list, string? directory, string? text, CancellationToken ct = default)
    {
        if (ParseList(list) is not { } parsed)
            return FleetError.ValidationError("Memory.List", "\"list\" is \"repository\" or \"machine\".");
        if (Validate(text) is { } invalid)
            return invalid;
        if (parsed == MemoryList.Repository && string.IsNullOrWhiteSpace(directory))
            return FleetError.ValidationError("Memory.Repository", "Say which repository the note is for.");

        var repository = parsed == MemoryList.Repository ? AgentMemory.RepositoryOf(directory!) : null;
        var notes = await store.ListForAsync(user.UserId, repository, ct).ConfigureAwait(false);
        var days = await store.ListDaysUsedAsync(user.UserId, repository, ct).ConfigureAwait(false);
        var activeNotes = Active(notes, days);
        if (await IsSearchOnAsync().ConfigureAwait(false))
        {
            if ((await FitAsync(user.UserId, [.. activeNotes.Where(note => note.List == parsed)], parsed, 1, ct).ConfigureAwait(false)).Error is { } noRoom)
                return noRoom;
        }
        else if (IsFull(activeNotes, parsed, repository))
        {
            return FleetError.ValidationError("Memory.Full", FullMessage(parsed));
        }

        var now = _time.GetUtcNow();
        var note = new MemoryNote(NewId(), parsed, text!.Trim(), MemoryKinds.Added, repository, null, null, now, now);
        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return ToView(note);
    }

    public async Task<Result<MemoryNoteView>> UpdateAsync(string id, string? text, CancellationToken ct = default)
    {
        if (Validate(text) is { } invalid)
            return invalid;

        if (await store.FindAsync(user.UserId, id, ct).ConfigureAwait(false) is not { } existing)
            return FleetError.NotFoundFor("MemoryNote", id);

        var note = existing with { Text = text!.Trim(), Updated = _time.GetUtcNow() };
        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return ToView(note);
    }

    public async Task<Result<Unit>> ForgetAsync(string id, CancellationToken ct = default)
    {
        if (await store.FindAsync(user.UserId, id, ct).ConfigureAwait(false) is not { } note)
            return FleetError.NotFoundFor("MemoryNote", id);

        await store.DeleteAsync(user.UserId, [id], ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>
    /// Moves a note between the group every session reads and the group kept for search, from Settings. Pinning a note
    /// to "always read" (<paramref name="toSearch"/> false) pins it: Fleet never moves it down by itself; when that group
    /// is full the least used unpinned note makes room. Moving a note to search unpins it.
    /// </summary>
    public async Task<Result<MemoryNoteView>> MoveAsync(string id, bool toSearch, CancellationToken ct = default)
    {
        if (await store.FindAsync(user.UserId, id, ct).ConfigureAwait(false) is not { } existing)
            return FleetError.NotFoundFor("MemoryNote", id);

        var note = existing with { InSearch = toSearch, Pinned = !toSearch };
        if (toSearch)
        {
            var peers = await ActivePeersAsync(existing, ct).ConfigureAwait(false);
            if (peers.Count(peer => !MemoryTiers.IsAlways(peer) && peer.Id != existing.Id) >= MemoryTiers.SearchCap(existing.List))
                return FleetError.ValidationError("Memory.Full", TieredFullMessage(existing.List));
        }
        else if (!MemoryTiers.IsAlways(existing))
        {
            var peers = await ActivePeersAsync(existing, ct).ConfigureAwait(false);
            if ((await FitAsync(user.UserId, [.. peers.Where(peer => peer.Id != existing.Id)], existing.List, 1, ct).ConfigureAwait(false)).Error is { } noRoom)
                return noRoom;
        }

        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return ToView(note);
    }

    private async Task<List<MemoryNote>> ActivePeersAsync(MemoryNote note, CancellationToken ct)
    {
        var notes = await store.ListForAsync(user.UserId, note.Repository, ct).ConfigureAwait(false);
        var days = await store.ListDaysUsedAsync(user.UserId, note.Repository, ct).ConfigureAwait(false);
        return [.. Active(notes.Where(other => other.List == note.List), days)];
    }

    /// <summary>
    /// Keeps a learned note for good: it no longer expires, and one that had expired comes back. For a lesson the user
    /// knows will stay true, so it isn't paid for again with a failure.
    /// </summary>
    public async Task<Result<MemoryNoteView>> KeepAsync(string id, CancellationToken ct = default)
    {
        if (await store.FindAsync(user.UserId, id, ct).ConfigureAwait(false) is not { } existing)
            return FleetError.NotFoundFor("MemoryNote", id);
        if (existing.Lifetime is null)
            return ToView(existing);

        var note = existing with { Lifetime = null };
        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return ToView(note);
    }

    /// <summary>
    /// Deletes every note (<paramref name="scope"/> <c>all</c>), the machine's (<c>machine</c>), or one repository's
    /// (<c>repository</c>, with <paramref name="directory"/>). Returns how many were deleted.
    /// </summary>
    public async Task<Result<int>> ClearAsync(string? scope, string? directory, CancellationToken ct = default)
    {
        IEnumerable<MemoryNote> doomed;
        Touched touched;
        switch (scope)
        {
            case "all":
                touched = Touched.Everything;
                doomed = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
                break;
            case "machine":
                touched = Touched.MachineList;
                doomed = MachineNotes(await store.ListForAsync(user.UserId, null, ct).ConfigureAwait(false));
                break;
            case "repository" when !string.IsNullOrWhiteSpace(directory):
                var repository = AgentMemory.RepositoryOf(directory);
                touched = new Touched(Machine: false, repository);
                doomed = RepositoryNotes(await store.ListForAsync(user.UserId, repository, ct).ConfigureAwait(false), repository);
                break;
            default:
                return FleetError.ValidationError("Memory.Scope", "\"scope\" is \"all\", \"machine\", or \"repository\" with a \"repository\".");
        }

        var deleted = await store.DeleteAsync(user.UserId, [.. doomed.Select(note => note.Id)], ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, touched, ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Saves a note an agent in <paramref name="session"/> asked for, as its owner. A note that repeats one in the same
    /// list confirms that one instead of adding another. <paramref name="replaces"/> swaps a wrong note for this one;
    /// it must be the machine's or this repository's. <paramref name="how"/> says how the agent knows a learned note holds. A full list refuses until the agent forgets or merges a note.
    /// </summary>
    public async Task<Result<MemorySaveOutcome>> SaveFromAgentAsync(
        Session session,
        string? list,
        string? text,
        string? kind,
        string? replaces,
        string? how = null,
        CancellationToken ct = default)
    {
        if (ParseList(list) is not { } parsed)
            return FleetError.ValidationError("Memory.List", "\"list\" is \"repository\" or \"machine\".");
        if (Validate(text) is { } invalid)
            return invalid;

        var userId = session.UserId;
        var repository = AgentMemory.RepositoryOf(session.Directory);
        var notes = await store.ListForAsync(userId, repository, ct).ConfigureAwait(false);
        var days = await store.ListDaysUsedAsync(userId, repository, ct).ConfigureAwait(false);
        var reachable = RepositoryNotes(notes, repository).Concat(MachineNotes(notes)).ToList();
        var active = Active(reachable, days);
        var trimmed = text!.Trim();
        var noteKind = MemoryKinds.IsAgentKind(kind) ? kind! : MemoryKinds.Learned;
        var now = _time.GetUtcNow();
        // Only a learned note says how it's known: what the user said needs no test.
        var noteHow = noteKind == MemoryKinds.Learned ? AgentMemory.CleanHow(how) : null;

        MemoryNote? replaced = null;
        if (!string.IsNullOrWhiteSpace(replaces))
        {
            replaced = reachable.FirstOrDefault(note => note.Id == replaces.Trim());
            if (replaced is null)
                return FleetError.ValidationError("Memory.Replaces", $"There's no note {replaces} for this repository or this machine. Use an id from the list in your instructions.");
        }

        var noteRepository = parsed == MemoryList.Repository ? repository : null;
        var same = active.FirstOrDefault(note =>
            note.List == parsed
            && note.Id != replaced?.Id
            && string.Equals(note.Text, trimmed, StringComparison.OrdinalIgnoreCase));
        if (same is not null && replaced is null)
        {
            // Saying it again confirms it: it stays, dated today, and its days start again.
            var confirmed = same with { Updated = now, How = noteHow ?? same.How };
            await store.SaveAsync(userId, confirmed, ct).ConfigureAwait(false);
            await RefreshContextAsync(userId, Touched.Of(confirmed), ct).ConfigureAwait(false);
            return new MemorySaveOutcome(confirmed, null, AlreadyKnown: true);
        }

        // The same lesson learned again after it expired: the failure came back, so the old note returns, in the new
        // words, and lasts twice as long.
        var relearned = replaced is null && noteKind == MemoryKinds.Learned
            ? reachable
                .Where(note => note.List == parsed && note.Kind == MemoryKinds.Learned && IsExpired(note, days))
                .Select(note => (Note: note, Score: MemoryText.Similarity(note.Text, trimmed)))
                .Where(match => match.Score >= MemoryText.SameLesson)
                .OrderByDescending(match => match.Score)
                .Select(match => match.Note)
                .FirstOrDefault()
            : null;

        IReadOnlyList<MemoryNote> movedDown = [];
        if (await IsSearchOnAsync().ConfigureAwait(false))
        {
            // A note that takes no one's place goes in the group every session reads; when that's full, the least used moves to search.
            if (replaced is null || replaced.List != parsed)
            {
                var fit = await FitAsync(userId, [.. active.Where(other => other.List == parsed && other.Id != relearned?.Id)], parsed, 1, ct).ConfigureAwait(false);
                if (fit.Error is { } noRoom)
                    return noRoom;
                movedDown = fit.Moved;
            }
        }
        else if (replaced is null && IsFull(active, parsed, noteRepository))
        {
            return FleetError.ValidationError("Memory.Full", FullMessage(parsed));
        }

        var previous = replaced ?? relearned;
        var place = replaced?.List == parsed ? replaced : null;
        var note = new MemoryNote(
            previous?.List == parsed ? previous.Id : NewId(),
            parsed,
            trimmed,
            noteKind,
            noteRepository,
            session.Id,
            session.Title,
            previous?.List == parsed ? previous.Created : now,
            now,
            Lifetime(noteKind, replaced, relearned),
            (previous?.Relearned ?? 0) + (relearned is null ? 0 : 1),
            noteHow,
            place?.InSearch ?? false,
            place?.Pinned ?? false,
            previous?.List == parsed ? previous.Found : 0,
            previous?.List == parsed ? previous.LastFound : null);

        if (replaced is not null && replaced.Id != note.Id)
            await store.DeleteAsync(userId, [replaced.Id], ct).ConfigureAwait(false);
        await store.SaveAsync(userId, note, ct).ConfigureAwait(false);
        await PruneExpiredAsync(userId, [.. reachable.Where(other => other.Id != note.Id && other.List == parsed)], days, parsed, ct)
            .ConfigureAwait(false);
        told?.Saved(session.Id, note, replaced?.Id);
        // A note moved between lists changes both.
        var touched = replaced is null ? Touched.Of(note) : Touched.Of(note).And(Touched.Of(replaced));
        // A note moved down is still in the same list: the refresh below covers it.
        await RefreshContextAsync(userId, touched, ct).ConfigureAwait(false);
        await BroadcastSavedAsync(userId, note, replaced, ct).ConfigureAwait(false);
        return new MemorySaveOutcome(note, replaced?.Id, AlreadyKnown: false, Relearned: relearned is not null, MovedDown: movedDown);
    }

    /// <summary>
    /// How long a note an agent saves lasts. A lesson it learned lasts <see cref="AgentMemory.LearnedLifetimeDays"/>
    /// days of use, twice what it had when it was learned again, and what it had when the agent only corrects it;
    /// what the user said never expires.
    /// </summary>
    private static int? Lifetime(string kind, MemoryNote? replaced, MemoryNote? relearned)
    {
        if (kind != MemoryKinds.Learned)
            return null;
        if (relearned is not null)
            return AgentMemory.RelearnedLifetime(relearned.Lifetime);
        if (replaced is { Kind: MemoryKinds.Learned })
            return replaced.Lifetime;
        return AgentMemory.LearnedLifetimeDays;
    }

    /// <summary>
    /// Fleet keeps expired notes so a lesson learned again lasts longer, but not without end: past as many as the list
    /// holds, the ones that expired longest ago go.
    /// </summary>
    private async Task PruneExpiredAsync(string userId, IReadOnlyList<MemoryNote> list, MemoryDaysUsed days, MemoryList which, CancellationToken ct)
    {
        var doomed = list
            .Where(note => IsExpired(note, days))
            .OrderByDescending(note => note.Updated)
            .Skip(AgentMemory.MaxNotes(which))
            .Select(note => note.Id)
            .ToList();
        if (doomed.Count > 0)
            await store.DeleteAsync(userId, doomed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>fleet_memory_search</c>: the notes of this repository and/or this machine that match <paramref name="query"/>,
    /// from both groups, best first (<see cref="MemoryTiers.Search"/>). Each note returned counts one more find, which is
    /// what moves a note down last and up first. Expired notes aren't searched.
    /// </summary>
    public async Task<Result<MemorySearchOutcome>> SearchFromAgentAsync(Session session, string? query, string? list, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return FleetError.ValidationError("Memory.Query", "\"query\" is required: a few words about what you're looking for.");
        var which = list?.Trim().ToLowerInvariant();
        if (which is not ("repository" or "machine" or "both"))
            return FleetError.ValidationError("Memory.List", "\"list\" is \"repository\", \"machine\" or \"both\".");

        var repository = AgentMemory.RepositoryOf(session.Directory);
        var notes = await store.ListForAsync(session.UserId, repository, ct).ConfigureAwait(false);
        var days = await store.ListDaysUsedAsync(session.UserId, repository, ct).ConfigureAwait(false);
        var searched = Active(RepositoryNotes(notes, repository).Concat(MachineNotes(notes)), days)
            .Where(note => which == "both" || note.List == (which == "machine" ? MemoryList.Machine : MemoryList.Repository))
            .ToList();

        var now = _time.GetUtcNow();
        List<MemoryNote> found = [];
        foreach (var hit in MemoryTiers.Search(searched, query, AgentMemory.MaxSearchResults))
        {
            var counted = hit with { Found = hit.Found + 1, LastFound = now };
            await store.SaveAsync(session.UserId, counted, ct).ConfigureAwait(false);
            found.Add(counted);
        }

        return new MemorySearchOutcome(found, searched.Count);
    }

    /// <summary>Forgets a note an agent in <paramref name="session"/> asked to: the machine's or this repository's.</summary>
    public async Task<Result<MemoryNote>> ForgetFromAgentAsync(Session session, string? id, CancellationToken ct = default)
    {
        var repository = AgentMemory.RepositoryOf(session.Directory);
        var notes = await store.ListForAsync(session.UserId, repository, ct).ConfigureAwait(false);
        var note = RepositoryNotes(notes, repository).Concat(MachineNotes(notes)).FirstOrDefault(note => note.Id == id?.Trim());
        if (note is null)
            return FleetError.NotFoundFor("MemoryNote", id ?? string.Empty);

        await store.DeleteAsync(session.UserId, [note.Id], ct).ConfigureAwait(false);
        told?.Forgot(session.Id, note.Id);
        await RefreshContextAsync(session.UserId, Touched.Of(note), ct).ConfigureAwait(false);
        return note;
    }

    /// <summary>
    /// Before a prompt: writes what a session in <paramref name="directory"/> reads, and returns it for a harness that
    /// takes it with the prompt (<paramref name="canSave"/> is whether that harness has the memory tools). Returns
    /// <see langword="null"/> when memory is off. Never throws for a file problem: the prompt goes without notes.
    /// </summary>
    public async Task<string?> PrepareSessionAsync(string userId, string directory, bool canSave, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !await IsEnabledAsync().ConfigureAwait(false))
            return null;

        try
        {
            var repository = AgentMemory.RepositoryOf(directory);
            await store.RecordDayUsedAsync(userId, repository, AgentMemory.Day(_time.GetUtcNow(), _time.LocalTimeZone), ct)
                .ConfigureAwait(false);
            var notes = await store.ListForAsync(userId, repository, ct).ConfigureAwait(false);
            var days = await store.ListDaysUsedAsync(userId, repository, ct).ConfigureAwait(false);
            var repositoryNotes = Active(RepositoryNotes(notes, repository), days);
            var machineNotes = Active(MachineNotes(notes), days);
            var searchOn = await IsSearchOnAsync().ConfigureAwait(false);
            if (searchOn)
            {
                await FitAsync(userId, repositoryNotes, MemoryList.Repository, 0, ct).ConfigureAwait(false);
                await FitAsync(userId, machineNotes, MemoryList.Machine, 0, ct).ConfigureAwait(false);
            }

            var (repositoryRead, repositoryMore) = MemoryTiers.Choose(repositoryNotes, MemoryList.Repository, searchOn);
            var (machineRead, machineMore) = MemoryTiers.Choose(machineNotes, MemoryList.Machine, searchOn);
            await store.WriteContextAsync(userId, directory, repository, AgentMemoryPrompt.RenderFolder(repository, repositoryRead, searchable: searchOn ? repositoryMore : null), ct)
                .ConfigureAwait(false);
            await store.WriteMachineContextAsync(userId, AgentMemoryPrompt.RenderMachine(machineRead, searchOn ? machineMore : null), ct).ConfigureAwait(false);
            return AgentMemoryPrompt.Render(repository, repositoryRead, machineRead, canSave, searchOn ? repositoryMore : null, searchOn ? machineMore : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (logger is not null)
                LogPrepareFailed(logger, directory, ex);
            return null;
        }
    }

    /// <summary>
    /// Before a prompt to a session whose harness passes Fleet's notes on to the model
    /// (<see cref="Domain.Harnesses.PromptOptions.ModelNotes"/>): what changed in its notes since it was last told, as
    /// a note for the model, or <see langword="null"/>. Its instructions keep the notes it started with, so this is how
    /// it hears about a note saved, reworded or forgotten elsewhere, or memory being turned off. Never throws for a
    /// file problem: the prompt goes without it.
    /// </summary>
    public async Task<string?> ChangesForAsync(Session session, CancellationToken ct = default)
    {
        if (told is null || string.IsNullOrWhiteSpace(session.Directory))
            return null;
        if (!await IsEnabledAsync().ConfigureAwait(false))
            return told.TurnedOff(session.Id) ? AgentMemoryPrompt.TurnedOff : null;

        try
        {
            var repository = AgentMemory.RepositoryOf(session.Directory);
            var notes = await store.ListForAsync(session.UserId, repository, ct).ConfigureAwait(false);
            var days = await store.ListDaysUsedAsync(session.UserId, repository, ct).ConfigureAwait(false);
            var reachable = RepositoryNotes(notes, repository).Concat(MachineNotes(notes)).ToList();
            // A note that expired isn't wrong, only unconfirmed: the session keeps it rather than hear it's no longer true.
            // A note not read (moved to search, or past the cap) is dropped the same way: it needs no message.
            var searchOn = await IsSearchOnAsync().ConfigureAwait(false);
            var active = reachable.Where(note => !IsExpired(note, days)).ToList();
            var read = MemoryTiers.Choose([.. active.Where(note => note.List == MemoryList.Repository)], MemoryList.Repository, searchOn).Read
                .Concat(MemoryTiers.Choose([.. active.Where(note => note.List == MemoryList.Machine)], MemoryList.Machine, searchOn).Read)
                .ToList();
            var readIds = read.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            var unread = reachable.Where(note => !readIds.Contains(note.Id)).Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            return told.Tell(session.Id, read, unread) is { } changes
                ? AgentMemoryPrompt.RenderChanges(changes)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (logger is not null)
                LogPrepareFailed(logger, session.Directory, ex);
            return null;
        }
    }

    /// <summary>
    /// After the notes changed, writes again what reads them: the machine's file when the machine's list changed, and the
    /// files of the session folders in the repository that changed (every repository's, for <see cref="Touched.Everything"/>).
    /// Each repository's notes are read and rendered once, however many of its folders there are. A folder that no longer
    /// exists is forgotten.
    /// </summary>
    private async Task RefreshContextAsync(string userId, Touched touched, CancellationToken ct)
    {
        if (!await IsEnabledAsync().ConfigureAwait(false))
            return;

        var searchOn = await IsSearchOnAsync().ConfigureAwait(false);
        if (touched.Machine)
        {
            var days = await store.ListDaysUsedAsync(userId, null, ct).ConfigureAwait(false);
            var machine = Active(MachineNotes(await store.ListForAsync(userId, null, ct).ConfigureAwait(false)), days);
            var (machineRead, machineMore) = MemoryTiers.Choose(machine, MemoryList.Machine, searchOn);
            await store.WriteMachineContextAsync(userId, AgentMemoryPrompt.RenderMachine(machineRead, searchOn ? machineMore : null), ct).ConfigureAwait(false);
        }

        // Checking the folders is a lookup each, no writes, so it happens on every change.
        var folders = await store.ListContextFoldersAsync(userId, ct).ConfigureAwait(false);
        foreach (var gone in folders.Where(folder => !Directory.Exists(folder.Directory)).Select(folder => folder.Directory).Distinct().ToList())
            await store.ForgetContextAsync(userId, gone, ct).ConfigureAwait(false);

        if (!touched.AllRepositories && touched.Repository is null)
            return;

        var affected = folders
            .Where(folder => Directory.Exists(folder.Directory))
            .Where(folder => touched.AllRepositories || string.Equals(folder.Repository, touched.Repository, PathComparison))
            .GroupBy(folder => folder.Repository, PathComparer);
        foreach (var group in affected)
        {
            var notes = await store.ListForAsync(userId, group.Key, ct).ConfigureAwait(false);
            var days = await store.ListDaysUsedAsync(userId, group.Key, ct).ConfigureAwait(false);
            var (read, more) = MemoryTiers.Choose(Active(RepositoryNotes(notes, group.Key), days), MemoryList.Repository, searchOn);
            var content = AgentMemoryPrompt.RenderFolder(group.Key, read, searchable: searchOn ? more : null);
            foreach (var folder in group)
                await store.WriteContextAsync(userId, folder.Directory, folder.Repository, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a dream (or its Undo) changed notes: writes again what sessions read for the repository and the machine, so
    /// sessions started next, and running ones with their next prompt (<see cref="ChangesForAsync"/>), hear the change.
    /// </summary>
    public Task RefreshAfterDreamAsync(string userId, string repository, CancellationToken ct = default)
        => RefreshContextAsync(userId, Touched.MachineList.And(new Touched(Machine: false, repository)), ct);

    /// <summary>What a change touched: the machine's list, one repository's, or everything.</summary>
    private sealed record Touched(bool Machine, string? Repository, bool AllRepositories = false)
    {
        public static readonly Touched Everything = new(Machine: true, Repository: null, AllRepositories: true);

        public static readonly Touched MachineList = new(Machine: true, Repository: null);

        public static Touched Of(MemoryNote note)
            => note.List == MemoryList.Machine ? MachineList : new Touched(Machine: false, note.Repository);

        public Touched And(Touched other) => new(Machine || other.Machine, Repository ?? other.Repository, AllRepositories || other.AllRepositories);
    }

    private async Task BroadcastSavedAsync(string userId, MemoryNote note, MemoryNote? replaced, CancellationToken ct)
    {
        if (broadcaster is null)
            return;

        var payload = new MemorySavedPayload(
            ToView(note),
            note.Repository is { } repository ? AgentMemory.RepositoryName(repository) : null,
            replaced is null ? null : ToView(replaced));
        await broadcaster.BroadcastAsync(
                "sessions",
                AgentMemory.SavedEventType,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.MemorySavedPayload),
                userId,
                ct)
            .ConfigureAwait(false);
    }

    private static List<MemoryNote> RepositoryNotes(IEnumerable<MemoryNote> notes, string repository)
        => [.. notes.Where(note => note.List == MemoryList.Repository && string.Equals(note.Repository, repository, PathComparison))];

    private static List<MemoryNote> MachineNotes(IEnumerable<MemoryNote> notes)
        => [.. notes.Where(note => note.List == MemoryList.Machine)];

    private bool IsExpired(MemoryNote note, MemoryDaysUsed days) => AgentMemory.IsExpired(note, days.For(note), _time.LocalTimeZone);

    /// <summary>The notes sessions read: the ones that haven't expired.</summary>
    private List<MemoryNote> Active(IEnumerable<MemoryNote> notes, MemoryDaysUsed days) => [.. notes.Where(note => !IsExpired(note, days))];

    /// <summary>
    /// With search on: moves the always-read notes of one list that don't fit (more than the cap, less <paramref name="reserve"/>
    /// seats kept for a note about to be added) to search, in the order of <see cref="MemoryTiers.PickToMoveDown"/>, updating
    /// <paramref name="active"/> as it goes. The error says why it couldn't make room: every note pinned, or the search group full.
    /// </summary>
    private async Task<(List<MemoryNote> Moved, FleetError? Error)> FitAsync(string userId, List<MemoryNote> active, MemoryList list, int reserve, CancellationToken ct)
    {
        List<MemoryNote> moved = [];
        while (active.Count(MemoryTiers.IsAlways) + reserve > MemoryTiers.AlwaysCap(list))
        {
            if (active.Count(note => !MemoryTiers.IsAlways(note)) >= MemoryTiers.SearchCap(list))
                return (moved, reserve == 0 ? null : FleetError.ValidationError("Memory.Full", TieredFullMessage(list)));
            if (MemoryTiers.PickToMoveDown(active.Where(MemoryTiers.IsAlways)) is not { } victim)
            {
                return (moved, reserve == 0 ? null : FleetError.ValidationError(
                    "Memory.Pinned", $"Every note the {ListName(list)} list has in the group every session reads is pinned, so there's no room. Unpin a note in Settings → Memory, or forget one."));
            }

            var down = victim with { InSearch = true };
            await store.SaveAsync(userId, down, ct).ConfigureAwait(false);
            active[active.FindIndex(note => note.Id == victim.Id)] = down;
            moved.Add(down);
        }

        return (moved, null);
    }

    private static string ListName(MemoryList list) => list == MemoryList.Machine ? "machine" : "repository";

    private static bool IsFull(IReadOnlyList<MemoryNote> notes, MemoryList list, string? repository)
        => (list == MemoryList.Machine ? MachineNotes(notes).Count : RepositoryNotes(notes, repository!).Count) >= AgentMemory.MaxNotes(list);

    private static string TieredFullMessage(MemoryList list)
        => $"The {ListName(list)} list is full ({MemoryTiers.AlwaysCap(list)} notes every session reads and {MemoryTiers.SearchCap(list)} kept for search). "
           + "Forget a note that's out of date, or save a merged note with \"replaces\" set to one it covers.";

    private static string FullMessage(MemoryList list)
        => $"The {(list == MemoryList.Machine ? "machine" : "repository")} list is full ({AgentMemory.MaxNotes(list)} notes). "
           + "Forget a note that's out of date, or save a merged note with \"replaces\" set to one it covers.";

    private static FleetError? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FleetError.ValidationError("Memory.Text", "\"text\" is required: the note, in a sentence or two.");
        if (text.Trim().Length > AgentMemory.MaxNoteLength)
            return FleetError.ValidationError("Memory.Text", $"A note is at most {AgentMemory.MaxNoteLength} characters. Keep it to one fact, in a sentence or two.");
        return null;
    }

    private static MemoryList? ParseList(string? list) => list?.Trim().ToLowerInvariant() switch
    {
        "repository" => MemoryList.Repository,
        "machine" => MemoryList.Machine,
        _ => null,
    };

    /// <summary>Four characters per token is the usual rough measure for English text.</summary>
    private static int EstimateTokens(string text) => (text.Length + 3) / 4;

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>The note as the API shows it, with its days left counted in <paramref name="days"/> (none: it was just saved).</summary>
    private MemoryNoteView ToView(MemoryNote note, MemoryDaysUsed? days = null)
    {
        var used = days is null ? 0 : AgentMemory.DaysUsedSince(note, days.For(note), _time.LocalTimeZone);
        return new MemoryNoteView(
            note.Id,
            note.List == MemoryList.Machine ? "machine" : "repository",
            note.Text,
            note.Kind,
            note.Repository,
            note.SessionId,
            note.SessionTitle,
            note.Created,
            note.Updated,
            note.Lifetime,
            note.Lifetime is { } lifetime ? Math.Max(lifetime - used, 0) : null,
            note.Lifetime is { } limit && used > limit,
            note.Relearned,
            note.How,
            note.InSearch && !note.Pinned,
            note.Pinned,
            note.Found,
            note.LastFound);
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the memory notes for {Directory}; the prompt goes without them")]
    private static partial void LogPrepareFailed(ILogger logger, string directory, Exception exception);
}
