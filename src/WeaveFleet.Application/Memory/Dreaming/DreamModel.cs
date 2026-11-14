using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>
/// Dreaming: a pass that reads a repository's recent sessions, finds lessons that keep coming up and changes its memory
/// notes itself, with safety rules enforced in code (<see cref="DreamApplier"/>). Part of Memory: available whenever Memory is on, with no switch of its own; the nightly run stays opt-in.
/// </summary>
public static class DreamRules
{
    public const string TurnedOffMessage = "Memory is off, so Fleet can't dream. Turn on Memory in Settings → Memory.";

    /// <summary>The event on the <c>sessions</c> topic when a dream ends, with the <see cref="DreamView"/>.</summary>
    public const string DreamedEventType = "memory.dreamed";

    /// <summary>The tag every dreaming session carries; dreams never read these.</summary>
    public const string SessionTag = "dreaming";

    public const string SourceReferencePrefix = "dream:";

    public const int SessionsRead = 20;

    public const int MaxSessionChars = 4_000;

    public const int MaxTotalChars = 60_000;

    public const int ListLimit = 10;

    /// <summary>The user preference holding a night's token budget (<see cref="DefaultBudgetTokens"/> when unset).</summary>
    public const string BudgetPreferenceKey = "DreamingBudget";

    public const int DefaultBudgetTokens = 300_000;

    /// <summary>Why a night's dream failed when its session used what was left of the night's budget.</summary>
    public const string StoppedAtBudget = "Stopped at the night's token budget.";

    /// <summary>The most sessions a night looks at to find the repositories worked in.</summary>
    public const int NightSessionsScanned = 500;

    /// <summary>How many distinct sessions must have relied on a learned note before a dream may keep it for good.</summary>
    public const int KeepForGoodSessions = 3;

    /// <summary>
    /// The harnesses whose permission gate Fleet can make read-only for a dreaming session: they may read the repository
    /// (<see cref="WeaveFleet.Domain.Harnesses.PermissionPolicy.ReadOnlyIn"/>). Any other dreams from the transcripts alone.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnlyHarnesses = new HashSet<string>(StringComparer.Ordinal) { "opencode2", "claude-code" };

    public static class Statuses
    {
        public const string Reading = "reading";
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";
    }

    public static class ChangeKinds
    {
        public const string Added = "added";
        public const string Rewritten = "rewritten";

        /// <summary>An untested learned note the dream checked and found true: the text stays, only its <c>how</c> is new.</summary>
        public const string Confirmed = "confirmed";
        public const string Merged = "merged";
        public const string Retired = "retired";
        public const string Kept = "kept";
        public const string Left = "left";

        /// <summary>A note of the user's that looks out of date: shown with its evidence, changed in no way.</summary>
        public const string Flagged = "flagged";
        public const string Skipped = "skipped";
    }

    public static string ListName(MemoryList list) => list == MemoryList.Machine ? "machine" : "repository";

    /// <summary>SHA-256 hex of <c>{list}\n{kind}\n{text}</c>: what tells that a note changed.</summary>
    public static string HashOf(MemoryList list, string kind, string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{ListName(list)}\n{kind}\n{text}")));

    public static string HashOf(MemoryNote note) => HashOf(note.List, note.Kind, note.Text);

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}

/// <summary>Whether dreaming is available for the current user: whenever Memory is on.</summary>
public sealed class DreamingFeature(IUserPreferenceRepository preferences)
{
    public Task<bool> IsEnabledAsync() => new AgentMemoryFeature(preferences).IsEnabledAsync();
}

/// <summary>A session a dream read, under the label (<c>S1</c>…) the model knew it by.</summary>
public sealed class DreamSession
{
    public string Label { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string HarnessType { get; set; } = "";

    /// <summary>What the user asked in it (the <c>You:</c> lines the model read): a correction is checked against these.</summary>
    public List<string> Prompts { get; set; } = [];
}

/// <summary>A note as it was when the dream began, with the hash that tells whether it has changed since.</summary>
public sealed class DreamNote
{
    public string Id { get; set; } = "";
    public string List { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Hash { get; set; } = "";

    /// <summary>A learned note that will expire if unused: the only kind a dream may keep for good.</summary>
    public bool Fades { get; set; }

    /// <summary>How the note's author knew it holds, as kept on the note (null for a learned note nobody tested).</summary>
    public string? How { get; set; }
}

/// <summary>What a dream did with one proposal, with what Undo needs to put it back.</summary>
public sealed class DreamChange
{
    public string Id { get; set; } = DreamRules.NewId();
    public string Kind { get; set; } = "";
    public string? List { get; set; }
    public string? NoteId { get; set; }
    public string? Text { get; set; }

    /// <summary>The notes the change replaced, removed or wanted to change, as they were.</summary>
    public List<MemoryNote> Replaced { get; set; } = [];

    public string Why { get; set; } = "";
    public List<string> SessionIds { get; set; } = [];
    public string? Quote { get; set; }
    public string? Reason { get; set; }

    /// <summary>What the dream read to check a note: the file or commit, with what it showed. Rewrite, retire and flag carry it.</summary>
    public string? Evidence { get; set; }

    /// <summary>For a confirmed note: how it's now known to hold, as written on the note.</summary>
    public string? How { get; set; }

    /// <summary>For a flag: the hash of the note as flagged, so a later dream doesn't flag the same unchanged note again.</summary>
    public string? NoteHash { get; set; }

    /// <summary>Undone; for a flag, dismissed with Keep (there is nothing to put back).</summary>
    public bool Undone { get; set; }

    /// <summary>The hash of the note the dream wrote (added, rewritten, merged keep); Undo refuses when it no longer matches.</summary>
    public string? WroteHash { get; set; }
}

/// <summary>One dream: its repository, the session that dreamed it, what it read and what it changed.</summary>
public sealed class Dream
{
    public string Id { get; set; } = DreamRules.NewId();
    public string Repository { get; set; } = "";
    public string? SessionId { get; set; }
    public string? HarnessType { get; set; }
    public string Status { get; set; } = DreamRules.Statuses.Reading;
    public string? Error { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public List<DreamSession> ReadSessions { get; set; } = [];
    public List<DreamNote> Notes { get; set; } = [];
    public List<DreamChange> Changes { get; set; } = [];

    /// <summary>The automation run (a night) that started it; null for Dream now.</summary>
    public string? RunId { get; set; }

    /// <summary>For a night's dream: it read the sessions started since then (the last night). Null reads the newest.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>What the dreaming session cost, once it ended.</summary>
    public int? Tokens { get; set; }

    public bool IsActive => Status is DreamRules.Statuses.Reading or DreamRules.Statuses.Running;
}

public sealed record DreamSessionView(string Id, string Title, string HarnessType);

public sealed record DreamNoteView(string Id, string Text, string Kind, string List);

public sealed record DreamChangeView(
    string Id,
    string Kind,
    string? List,
    string? NoteId,
    string? Text,
    IReadOnlyList<DreamNoteView> Replaced,
    string Why,
    IReadOnlyList<DreamSessionView> Sessions,
    int OfSessions,
    string? Quote,
    string? Reason,
    bool Undone,
    string? Evidence = null,
    string? How = null);

public sealed record DreamView(
    string Id,
    string Repository,
    string RepositoryName,
    string? SessionId,
    string? HarnessType,
    string Status,
    string? Error,
    DateTimeOffset Started,
    DateTimeOffset? Finished,
    IReadOnlyList<DreamSessionView> ReadSessions,
    IReadOnlyList<DreamChangeView> Changes,
    string? RunId = null,
    int? Tokens = null,
    DateTimeOffset? Since = null)
{
    public static DreamView Of(Dream dream)
    {
        var read = dream.ReadSessions.Select(s => new DreamSessionView(s.Id, s.Title, s.HarnessType)).ToList();
        return new DreamView(
            dream.Id,
            dream.Repository,
            AgentMemory.RepositoryName(dream.Repository),
            dream.SessionId,
            dream.HarnessType,
            dream.Status,
            dream.Error,
            dream.Started,
            dream.Finished,
            read,
            [.. dream.Changes.Select(change => new DreamChangeView(
                change.Id,
                change.Kind,
                change.List,
                change.NoteId,
                change.Text,
                [.. change.Replaced.Select(n => new DreamNoteView(n.Id, n.Text, n.Kind, DreamRules.ListName(n.List)))],
                change.Why,
                [.. change.SessionIds.Select(id => read.FirstOrDefault(s => s.Id == id)).OfType<DreamSessionView>()],
                read.Count,
                change.Quote,
                change.Reason,
                change.Undone,
                change.Evidence,
                change.How))],
            dream.RunId,
            dream.Tokens,
            dream.Since);
    }
}

public sealed record DreamListView(IReadOnlyList<DreamView> Dreams);

/// <param name="Since">A night's dream reads the sessions started since then; null reads the newest.</param>
/// <param name="RunId">The night (automation run) starting it; set by Fleet only.</param>
/// <param name="TokenLimit">
/// What the dreaming session may use: Fleet stops it at the model call that reaches this. A night sets it to what's
/// left of its budget.
/// </param>
public sealed record StartDreamRequest(
    string? Repository,
    string? HarnessType,
    string? ProviderId,
    string? ModelId,
    DateTimeOffset? Since = null,
    string? RunId = null,
    long? TokenLimit = null);

/// <summary>Where a user's dreams are kept.</summary>
public interface IDreamStore
{
    /// <summary>The repository's dreams, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Dream>> ListAsync(string userId, string repository, int limit, CancellationToken ct = default);

    Task<Dream?> FindAsync(string userId, string id, CancellationToken ct = default);

    Task SaveAsync(string userId, Dream dream, CancellationToken ct = default);

    /// <summary>The dreams in every repository started at or after <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<Dream>> ListSinceAsync(string userId, DateTimeOffset since, int limit, CancellationToken ct = default);
}
