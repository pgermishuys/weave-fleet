using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
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
    int MaxMachineNotes);

/// <summary>A note as the API shows it. <see cref="List"/> is <c>repository</c> or <c>machine</c>.</summary>
public sealed record MemoryNoteView(
    string Id,
    string List,
    string Text,
    string Kind,
    string? Repository,
    string? SessionId,
    string? SessionTitle,
    DateTimeOffset Created,
    DateTimeOffset Updated);

/// <summary>What a session in <see cref="Repository"/> reads, and roughly what that costs per request.</summary>
public sealed record MemoryNotesView(
    string? Repository,
    IReadOnlyList<MemoryNoteView> RepositoryNotes,
    IReadOnlyList<MemoryNoteView> MachineNotes,
    int Tokens);

/// <summary>
/// What Fleet tells the user's windows when an agent saves a note, so they can offer Undo. <see cref="Previous"/> is the
/// note it replaced, as it was, so Undo can put it back.
/// </summary>
public sealed record MemorySavedPayload(MemoryNoteView Note, string? RepositoryName, MemoryNoteView? Previous);

/// <summary>The note an agent saved, and the one it replaced.</summary>
public sealed record MemorySaveOutcome(MemoryNote Note, string? Replaced, bool AlreadyKnown);

/// <summary>
/// The user's notes: Settings reads and edits them, agents save and forget them (<see cref="AgentMemoryBridge"/>), and
/// every prompt writes what its session reads (<see cref="PrepareSessionAsync"/>). After any change the files every
/// session folder reads are written again, so a running session sees the change on its next model request.
/// </summary>
public sealed partial class AgentMemoryService(
    IMemoryStore store,
    IUserPreferenceRepository preferences,
    IUserContext user,
    TimeProvider? time = null,
    IEventBroadcaster? broadcaster = null,
    ISessionRepository? sessions = null,
    ILogger<AgentMemoryService>? logger = null)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<bool> IsEnabledAsync() => new AgentMemoryFeature(preferences).IsEnabledAsync();

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
            AgentMemory.MaxMachineNotes);
    }

    /// <summary>
    /// Turns memory on or off. Off keeps the notes but deletes what sessions read, so running sessions stop getting
    /// them on their next request; sessions started afterwards run without the memory tools.
    /// </summary>
    public async Task<MemoryOverview> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await preferences.SetAsync(AgentMemory.PreferenceKey, enabled ? "true" : "false").ConfigureAwait(false);
        if (enabled)
            await RefreshContextAsync(user.UserId, ct).ConfigureAwait(false);
        else
            await store.ClearContextAsync(user.UserId, ct).ConfigureAwait(false);
        return await GetOverviewAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The notes a session in <paramref name="directory"/> reads: its repository's and the machine's.</summary>
    public async Task<MemoryNotesView> ListAsync(string? directory, CancellationToken ct = default)
    {
        var notes = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        var repository = string.IsNullOrWhiteSpace(directory) ? null : AgentMemory.RepositoryOf(directory);
        var repositoryNotes = repository is null ? [] : RepositoryNotes(notes, repository);
        var machineNotes = MachineNotes(notes);
        var tokens = repository is null
            ? 0
            : EstimateTokens(AgentMemoryPrompt.Render(repository, repositoryNotes, machineNotes));

        return new MemoryNotesView(
            repository,
            [.. repositoryNotes.OrderByDescending(note => note.Updated).Select(ToView)],
            [.. machineNotes.OrderByDescending(note => note.Updated).Select(ToView)],
            tokens);
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
        var notes = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        if (IsFull(notes, parsed, repository))
            return FleetError.ValidationError("Memory.Full", FullMessage(parsed));

        var now = _time.GetUtcNow();
        var note = new MemoryNote(NewId(), parsed, text!.Trim(), MemoryKinds.Added, repository, null, null, now, now);
        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, ct).ConfigureAwait(false);
        return ToView(note);
    }

    public async Task<Result<MemoryNoteView>> UpdateAsync(string id, string? text, CancellationToken ct = default)
    {
        if (Validate(text) is { } invalid)
            return invalid;

        var notes = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        if (notes.FirstOrDefault(note => note.Id == id) is not { } existing)
            return FleetError.NotFoundFor("MemoryNote", id);

        var note = existing with { Text = text!.Trim(), Updated = _time.GetUtcNow() };
        await store.SaveAsync(user.UserId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, ct).ConfigureAwait(false);
        return ToView(note);
    }

    public async Task<Result<Unit>> ForgetAsync(string id, CancellationToken ct = default)
    {
        if (await store.DeleteAsync(user.UserId, [id], ct).ConfigureAwait(false) == 0)
            return FleetError.NotFoundFor("MemoryNote", id);

        await RefreshContextAsync(user.UserId, ct).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>
    /// Deletes every note (<paramref name="scope"/> <c>all</c>), the machine's (<c>machine</c>), or one repository's
    /// (<c>repository</c>, with <paramref name="directory"/>). Returns how many were deleted.
    /// </summary>
    public async Task<Result<int>> ClearAsync(string? scope, string? directory, CancellationToken ct = default)
    {
        var notes = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        IEnumerable<MemoryNote> doomed;
        switch (scope)
        {
            case "all":
                doomed = notes;
                break;
            case "machine":
                doomed = MachineNotes(notes);
                break;
            case "repository" when !string.IsNullOrWhiteSpace(directory):
                doomed = RepositoryNotes(notes, AgentMemory.RepositoryOf(directory));
                break;
            default:
                return FleetError.ValidationError("Memory.Scope", "\"scope\" is \"all\", \"machine\", or \"repository\" with a \"repository\".");
        }

        var deleted = await store.DeleteAsync(user.UserId, [.. doomed.Select(note => note.Id)], ct).ConfigureAwait(false);
        await RefreshContextAsync(user.UserId, ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Saves a note an agent in <paramref name="session"/> asked for, as its owner. A note that repeats one in the same
    /// list confirms that one instead of adding another. <paramref name="replaces"/> swaps a wrong note for this one;
    /// it must be the machine's or this repository's. A full list refuses until the agent forgets or merges a note.
    /// </summary>
    public async Task<Result<MemorySaveOutcome>> SaveFromAgentAsync(
        Session session,
        string? list,
        string? text,
        string? kind,
        string? replaces,
        CancellationToken ct = default)
    {
        if (ParseList(list) is not { } parsed)
            return FleetError.ValidationError("Memory.List", "\"list\" is \"repository\" or \"machine\".");
        if (Validate(text) is { } invalid)
            return invalid;

        var userId = session.UserId;
        var repository = AgentMemory.RepositoryOf(session.Directory);
        var notes = await store.ListAsync(userId, ct).ConfigureAwait(false);
        var reachable = RepositoryNotes(notes, repository).Concat(MachineNotes(notes)).ToList();
        var trimmed = text!.Trim();
        var now = _time.GetUtcNow();

        MemoryNote? replaced = null;
        if (!string.IsNullOrWhiteSpace(replaces))
        {
            replaced = reachable.FirstOrDefault(note => note.Id == replaces.Trim());
            if (replaced is null)
                return FleetError.ValidationError("Memory.Replaces", $"There's no note {replaces} for this repository or this machine. Use an id from the list in your instructions.");
        }

        var noteRepository = parsed == MemoryList.Repository ? repository : null;
        var same = reachable.FirstOrDefault(note =>
            note.List == parsed
            && note.Id != replaced?.Id
            && string.Equals(note.Text, trimmed, StringComparison.OrdinalIgnoreCase));
        if (same is not null && replaced is null)
        {
            // Saying it again confirms it: it stays, dated today.
            var confirmed = same with { Updated = now };
            await store.SaveAsync(userId, confirmed, ct).ConfigureAwait(false);
            await RefreshContextAsync(userId, ct).ConfigureAwait(false);
            return new MemorySaveOutcome(confirmed, null, AlreadyKnown: true);
        }

        if (replaced is null && IsFull(notes, parsed, noteRepository))
            return FleetError.ValidationError("Memory.Full", FullMessage(parsed));

        var note = new MemoryNote(
            replaced?.List == parsed ? replaced.Id : NewId(),
            parsed,
            trimmed,
            MemoryKinds.IsAgentKind(kind) ? kind! : MemoryKinds.Learned,
            noteRepository,
            session.Id,
            session.Title,
            replaced?.List == parsed ? replaced.Created : now,
            now);

        if (replaced is not null && replaced.Id != note.Id)
            await store.DeleteAsync(userId, [replaced.Id], ct).ConfigureAwait(false);
        await store.SaveAsync(userId, note, ct).ConfigureAwait(false);
        await RefreshContextAsync(userId, ct).ConfigureAwait(false);
        await BroadcastSavedAsync(userId, note, replaced, ct).ConfigureAwait(false);
        return new MemorySaveOutcome(note, replaced?.Id, AlreadyKnown: false);
    }

    /// <summary>Forgets a note an agent in <paramref name="session"/> asked to: the machine's or this repository's.</summary>
    public async Task<Result<MemoryNote>> ForgetFromAgentAsync(Session session, string? id, CancellationToken ct = default)
    {
        var notes = await store.ListAsync(session.UserId, ct).ConfigureAwait(false);
        var repository = AgentMemory.RepositoryOf(session.Directory);
        var note = RepositoryNotes(notes, repository).Concat(MachineNotes(notes)).FirstOrDefault(note => note.Id == id?.Trim());
        if (note is null)
            return FleetError.NotFoundFor("MemoryNote", id ?? string.Empty);

        await store.DeleteAsync(session.UserId, [note.Id], ct).ConfigureAwait(false);
        await RefreshContextAsync(session.UserId, ct).ConfigureAwait(false);
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
            var notes = await store.ListAsync(userId, ct).ConfigureAwait(false);
            var repository = AgentMemory.RepositoryOf(directory);
            var repositoryNotes = RepositoryNotes(notes, repository);
            var machineNotes = MachineNotes(notes);
            await store.WriteContextAsync(userId, directory, AgentMemoryPrompt.Render(repository, repositoryNotes, machineNotes), ct).ConfigureAwait(false);
            return AgentMemoryPrompt.Render(repository, repositoryNotes, machineNotes, canSave);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (logger is not null)
                LogPrepareFailed(logger, directory, ex);
            return null;
        }
    }

    /// <summary>Writes again what every session folder reads, after the notes changed.</summary>
    private async Task RefreshContextAsync(string userId, CancellationToken ct)
    {
        if (!await IsEnabledAsync().ConfigureAwait(false))
            return;

        var notes = await store.ListAsync(userId, ct).ConfigureAwait(false);
        var machineNotes = MachineNotes(notes);
        foreach (var directory in await store.ListContextDirectoriesAsync(userId, ct).ConfigureAwait(false))
        {
            var repository = AgentMemory.RepositoryOf(directory);
            await store.WriteContextAsync(userId, directory, AgentMemoryPrompt.Render(repository, RepositoryNotes(notes, repository), machineNotes), ct)
                .ConfigureAwait(false);
        }
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

    private static bool IsFull(IReadOnlyList<MemoryNote> notes, MemoryList list, string? repository)
        => (list == MemoryList.Machine ? MachineNotes(notes).Count : RepositoryNotes(notes, repository!).Count) >= AgentMemory.MaxNotes(list);

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

    internal static MemoryNoteView ToView(MemoryNote note) => new(
        note.Id,
        note.List == MemoryList.Machine ? "machine" : "repository",
        note.Text,
        note.Kind,
        note.Repository,
        note.SessionId,
        note.SessionTitle,
        note.Created,
        note.Updated);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the memory notes for {Directory}; the prompt goes without them")]
    private static partial void LogPrepareFailed(ILogger logger, string directory, Exception exception);
}
