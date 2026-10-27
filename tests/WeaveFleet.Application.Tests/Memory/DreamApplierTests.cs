using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class DreamApplierTests
{
    private const string User = "owner";
    private const string Repo = "/work/fleet";
    private readonly FakeMemoryStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
    private readonly DreamApplier _applier;
    private readonly Dream _dream;

    public DreamApplierTests()
    {
        _applier = new DreamApplier(_store, _time);
        _dream = new Dream
        {
            Repository = Repo,
            SessionId = "dreaming-session",
            ReadSessions =
            [
                new DreamSession { Label = "S1", Id = "a", Title = "One", HarnessType = "opencode2", Prompts = ["No, use bun here. Never npm."] },
                new DreamSession { Label = "S2", Id = "b", Title = "Two", HarnessType = "claude-code" },
                new DreamSession { Label = "S3", Id = "c", Title = "Three", HarnessType = "opencode2" },
            ],
        };
    }

    private MemoryNote Note(string id, string kind, string text, MemoryList list = MemoryList.Repository)
    {
        var note = new MemoryNote(id, list, text, kind, list == MemoryList.Repository ? Repo : null, null, null, _time.GetUtcNow(), _time.GetUtcNow(),
            kind == MemoryKinds.Learned ? 7 : null);
        _store.Notes.Add(note);
        _dream.Notes.Add(new DreamNote { Id = id, List = DreamRules.ListName(list), Kind = kind, Text = text, Hash = DreamRules.HashOf(note) });
        return note;
    }

    private async Task<DreamChange> ApplyAsync(DreamProposal proposal)
        => (await ApplyAllAsync(proposal)).Single();

    private async Task<List<DreamChange>> ApplyAllAsync(params DreamProposal[] proposals)
    {
        var changes = await _applier.ApplyAsync(User, _dream, proposals);
        _dream.Changes.AddRange(changes);
        return changes;
    }

    private static DreamProposal Add(string text, params string[] sessions)
        => new() { Action = "add", List = "repository", Text = text, Why = "why", Sessions = [.. sessions] };

    [Fact]
    public async Task An_add_seen_in_two_sessions_is_applied_as_a_learned_note()
    {
        var change = await ApplyAsync(Add("Run tests with bun run test.", "S1", "S2"));

        change.Kind.ShouldBe("added");
        change.SessionIds.ShouldBe(["a", "b"]);
        var note = _store.Notes.Single();
        note.Kind.ShouldBe(MemoryKinds.Learned);
        note.Repository.ShouldBe(Repo);
        note.SessionId.ShouldBe("dreaming-session");
        note.Lifetime.ShouldBe(AgentMemory.LearnedLifetimeDays);
        change.NoteId.ShouldBe(note.Id);
        change.WroteHash.ShouldBe(DreamRules.HashOf(note));
    }

    [Fact]
    public async Task An_add_that_repeats_a_note_word_for_word_is_skipped()
    {
        Note("n1", MemoryKinds.Learned, "Run tests with bun run test.");

        var change = await ApplyAsync(Add("run tests with bun run test.", "S1", "S2"));

        change.Kind.ShouldBe("skipped");
        change.Reason.ShouldBe(DreamApplier.AlreadyANoteReason);
        _store.Notes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_add_seen_in_one_session_is_skipped_even_when_the_label_is_repeated()
    {
        var change = await ApplyAsync(Add("Run tests with bun run test.", "S1", "s1"));

        change.Kind.ShouldBe("skipped");
        change.Reason.ShouldBe("Seen in one session only.");
        _store.Notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_correction_with_a_quote_from_the_users_prompt_counts_as_evidence()
    {
        var change = await ApplyAsync(Add("Use bun, never npm.", "S1") with { Corrected = true, Quote = "never   NPM" });

        change.Kind.ShouldBe("added");
    }

    [Fact]
    public async Task A_correction_whose_quote_is_not_in_the_prompt_is_skipped()
    {
        var change = await ApplyAsync(Add("Use bun, never npm.", "S1") with { Corrected = true, Quote = "always pnpm" });

        change.Kind.ShouldBe("skipped");
        change.Reason.ShouldBe("Seen in one session only.");
    }

    [Fact]
    public async Task Bad_proposals_are_skipped_with_a_reason()
    {
        var changes = await ApplyAllAsync(
            new DreamProposal { Action = "explode" },
            Add("x", "S1", "S2") with { List = "galaxy" },
            Add("", "S1", "S2"),
            Add(new string('x', 401), "S1", "S2"),
            new DreamProposal { Action = "retire", Note = "nope", Sessions = ["S1", "S2"] });

        changes.ShouldAllBe(c => c.Kind == "skipped");
        changes.Select(c => c.Reason).ShouldAllBe(r => !string.IsNullOrEmpty(r));
    }

    [Theory]
    [InlineData("added")]
    [InlineData("from-you")]
    public async Task Rewriting_or_retiring_a_note_the_user_wrote_is_left(string kind)
    {
        Note("aa11", kind, "Use bun.");

        var rewrite = await ApplyAsync(new DreamProposal { Action = "rewrite", Note = "aa11", Text = "Use pnpm.", Sessions = ["S1", "S2"] });
        var retire = await ApplyAsync(new DreamProposal { Action = "retire", Note = "aa11", Sessions = ["S1", "S2"] });

        rewrite.Kind.ShouldBe("left");
        rewrite.Reason.ShouldBe(DreamApplier.UserNoteReason);
        rewrite.Replaced.Single().Text.ShouldBe("Use bun.");
        retire.Kind.ShouldBe("left");
        _store.Notes.Single().Text.ShouldBe("Use bun.");
    }

    [Fact]
    public async Task Merging_a_learned_duplicate_into_a_users_note_keeps_the_users_wording()
    {
        Note("aa11", "added", "Use bun, never npm.");
        Note("bb22", "learned", "Use bun instead of npm.");

        var change = await ApplyAsync(new DreamProposal { Action = "merge", Keep = "aa11", Remove = ["bb22"], Text = "Something else entirely." });

        change.Kind.ShouldBe("merged");
        _store.Notes.Single().Text.ShouldBe("Use bun, never npm.");
        change.Text.ShouldBe("Use bun, never npm.");
        change.WroteHash.ShouldBeNull();
        change.Replaced.Single().Id.ShouldBe("bb22");
    }

    [Fact]
    public async Task Merge_never_removes_a_users_note()
    {
        Note("aa11", "learned", "Use bun.");
        Note("bb22", "from-you", "Use bun, never npm.");

        var change = await ApplyAsync(new DreamProposal { Action = "merge", Keep = "aa11", Remove = ["bb22"] });

        change.Kind.ShouldBe("left");
        _store.Notes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_note_that_changed_while_dreaming_is_left()
    {
        var note = Note("aa11", "learned", "Use bun.");
        _store.Notes[0] = note with { Text = "Use bun, edited by the user." };

        var change = await ApplyAsync(new DreamProposal { Action = "rewrite", Note = "aa11", Text = "Use pnpm.", Sessions = ["S1", "S2"] });

        change.Kind.ShouldBe("left");
        change.Reason.ShouldBe(DreamApplier.ChangedReason);
        change.Replaced.Single().Text.ShouldBe("Use bun, edited by the user.");
        _store.Notes.Single().Text.ShouldBe("Use bun, edited by the user.");
    }

    [Fact]
    public async Task A_full_list_leaves_an_add()
    {
        for (var i = 0; i < AgentMemory.MaxMachineNotes; i++)
            _store.Notes.Add(new MemoryNote($"{i:x4}", MemoryList.Machine, $"note {i}", "added", null, null, null, _time.GetUtcNow(), _time.GetUtcNow()));

        var change = await ApplyAsync(Add("Another.", "S1", "S2") with { List = "machine" });

        change.Kind.ShouldBe("left");
        change.Reason.ShouldNotBeNull().ShouldContain("full");
        _store.Notes.Count.ShouldBe(AgentMemory.MaxMachineNotes);
    }

    [Fact]
    public async Task Undo_of_an_add_deletes_the_note_and_is_refused_once_it_was_edited()
    {
        var change = await ApplyAsync(Add("Run tests with bun run test.", "S1", "S2"));

        (await _applier.UndoAsync(User, _dream, change.Id)).ShouldBeNull();
        _store.Notes.ShouldBeEmpty();
        change.Undone.ShouldBeTrue();

        var again = await ApplyAsync(Add("Run tests with bun run test.", "S1", "S2"));
        _store.Notes[0] = _store.Notes[0] with { Text = "Edited." };
        var refused = await _applier.UndoAsync(User, _dream, again.Id);
        refused.ShouldNotBeNull().Description.ShouldBe(DreamApplier.UndoRefused);
        _store.Notes.Single().Text.ShouldBe("Edited.");
    }

    [Fact]
    public async Task Undo_of_a_rewrite_puts_the_old_note_back()
    {
        var original = Note("aa11", "learned", "Use bun.");
        var change = await ApplyAsync(new DreamProposal { Action = "rewrite", Note = "aa11", Text = "Use bun run test.", Sessions = ["S1", "S2"] });
        change.Kind.ShouldBe("rewritten");
        _store.Notes.Single().Text.ShouldBe("Use bun run test.");

        (await _applier.UndoAsync(User, _dream, change.Id)).ShouldBeNull();

        _store.Notes.Single().ShouldBe(original);
    }

    [Fact]
    public async Task Undo_of_a_retire_restores_the_note_unless_one_has_its_id_now()
    {
        var original = Note("aa11", "learned", "Use bun.");
        var change = await ApplyAsync(new DreamProposal { Action = "retire", Note = "aa11", Sessions = ["S1", "S2"] });
        change.Kind.ShouldBe("retired");
        _store.Notes.ShouldBeEmpty();

        (await _applier.UndoAsync(User, _dream, change.Id)).ShouldBeNull();
        _store.Notes.Single().ShouldBe(original);

        var second = await ApplyAsync(new DreamProposal { Action = "retire", Note = "aa11", Sessions = ["S1", "S2"] });
        _store.Notes.Add(original with { Text = "Written again." });
        (await _applier.UndoAsync(User, _dream, second.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Undo_of_a_merge_restores_the_removed_notes_and_the_old_keep()
    {
        var keep = Note("aa11", "learned", "Use bun.");
        var removed = Note("bb22", "learned", "Use bun, not npm.");
        var change = await ApplyAsync(new DreamProposal { Action = "merge", Keep = "aa11", Remove = ["bb22"], Text = "Use bun, never npm." });
        change.Kind.ShouldBe("merged");
        _store.Notes.Single().Text.ShouldBe("Use bun, never npm.");

        (await _applier.UndoAsync(User, _dream, change.Id)).ShouldBeNull();

        _store.Notes.OrderBy(n => n.Id).ToList().ShouldBe([keep, removed]);
    }

    [Fact]
    public async Task Only_changes_Fleet_made_can_be_undone()
    {
        var change = await ApplyAsync(Add("x", "S1"));

        (await _applier.UndoAsync(User, _dream, change.Id)).ShouldNotBeNull();
        (await _applier.UndoAsync(User, _dream, "nope")).ShouldNotBeNull();
    }
}
