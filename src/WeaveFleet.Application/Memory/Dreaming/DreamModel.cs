using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>
/// Dreaming: a pass that reads a repository's recent sessions, finds lessons that keep coming up and changes its memory
/// notes itself, with safety rules enforced in code (<see cref="DreamApplier"/>). Off unless the user turned it on.
/// </summary>
public static class DreamRules
{
    public const string PreferenceKey = "Dreaming";

    public const string TurnedOffMessage = "Dreaming is off. Turn it on in Settings → Features.";

    /// <summary>The event on the <c>sessions</c> topic when a dream ends, with the <see cref="DreamView"/>.</summary>
    public const string DreamedEventType = "memory.dreamed";

    /// <summary>The tag every dreaming session carries; dreams never read these.</summary>
    public const string SessionTag = "dreaming";

    public const string SourceReferencePrefix = "dream:";

    public const int SessionsRead = 20;

    public const int MaxSessionChars = 4_000;

    public const int MaxTotalChars = 60_000;

    public const int ListLimit = 10;

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
        public const string Merged = "merged";
        public const string Retired = "retired";
        public const string Left = "left";
        public const string Skipped = "skipped";
    }

    public static string ListName(MemoryList list) => list == MemoryList.Machine ? "machine" : "repository";

    /// <summary>SHA-256 hex of <c>{list}\n{kind}\n{text}</c>: what tells that a note changed.</summary>
    public static string HashOf(MemoryList list, string kind, string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{ListName(list)}\n{kind}\n{text}")));

    public static string HashOf(MemoryNote note) => HashOf(note.List, note.Kind, note.Text);

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}

/// <summary>Whether dreaming is on for the current user.</summary>
public sealed class DreamingFeature(IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync()
        => string.Equals(await preferences.GetAsync(DreamRules.PreferenceKey).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
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
    bool Undone);

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
    IReadOnlyList<DreamChangeView> Changes)
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
                change.Undone))]);
    }
}

public sealed record DreamListView(IReadOnlyList<DreamView> Dreams);

public sealed record StartDreamRequest(string? Repository, string? HarnessType, string? ProviderId, string? ModelId);

/// <summary>Where a user's dreams are kept.</summary>
public interface IDreamStore
{
    /// <summary>The repository's dreams, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Dream>> ListAsync(string userId, string repository, int limit, CancellationToken ct = default);

    Task<Dream?> FindAsync(string userId, string id, CancellationToken ct = default);

    Task SaveAsync(string userId, Dream dream, CancellationToken ct = default);
}
