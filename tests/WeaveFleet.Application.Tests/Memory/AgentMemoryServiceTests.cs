using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Memory;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class AgentMemoryServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-memory-").FullName;
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeMemoryStore _store = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero));
    private readonly AgentMemorySessions _told = new();
    private readonly AgentMemoryService _memory;
    private readonly string _repository;
    private readonly string _worktree;
    private readonly string _otherRepository;

    public AgentMemoryServiceTests()
    {
        _memory = new AgentMemoryService(_store, _preferences, new TestUserContext("owner"), _time, _broadcaster, told: _told);
        _repository = MakeRepository("weave-fleet");
        _otherRepository = MakeRepository("weave-cli");
        _worktree = Path.Combine(_root, "weave-fleet-worktrees", "feature");
        Directory.CreateDirectory(Path.Combine(_repository, ".git", "worktrees", "feature"));
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, ".git"), $"gitdir: {Path.Combine(_repository, ".git", "worktrees", "feature")}\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Memory_is_off_until_the_user_turns_it_on_and_then_sessions_get_their_notes()
    {
        (await _memory.PrepareSessionAsync("owner", _repository, canSave: false)).ShouldBeNull();
        _store.Context.ShouldBeEmpty();

        await _memory.SetEnabledAsync(true);
        var notes = await _memory.PrepareSessionAsync("owner", _repository, canSave: false);

        notes.ShouldNotBeNull();
        notes.ShouldContain("## This repository (weave-fleet)");
        notes.ShouldContain("## This machine", customMessage: "a harness that takes the notes with the prompt gets both parts");
        _store.Machine.ShouldNotBeNull().ShouldContain("## This machine");
        notes.ShouldNotContain("fleet_memory_save", customMessage: "a harness without the tools only reads the notes");
        _store.Context[_repository].ShouldContain("fleet_memory_save", customMessage: "the file OpenCode reads has the rules for saving");
    }

    [Fact]
    public async Task Turning_memory_off_keeps_the_notes_but_sessions_stop_reading_them()
    {
        await _memory.SetEnabledAsync(true);
        await _memory.AddAsync("machine", null, "/tmp fills up here; use ~/.cache.");
        await _memory.PrepareSessionAsync("owner", _repository, canSave: false);

        await _memory.SetEnabledAsync(false);

        _store.Context.ShouldBeEmpty();
        _store.Machine.ShouldBeNull();
        (await _memory.ListAsync(_repository)).MachineNotes.Single().Text.ShouldBe("/tmp fills up here; use ~/.cache.");
        (await _memory.PrepareSessionAsync("owner", _repository, canSave: false)).ShouldBeNull();
    }

    [Fact]
    public async Task A_note_an_agent_saves_reaches_every_session_folder_and_tells_the_users_windows()
    {
        await _memory.SetEnabledAsync(true);
        await _memory.PrepareSessionAsync("owner", _repository, canSave: false);
        await _memory.PrepareSessionAsync("owner", _otherRepository, canSave: false);

        var saved = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "WebFetch can't read github.com; use gh.", "learned", replaces: null);

        saved.IsSuccess.ShouldBeTrue();
        _store.Machine.ShouldNotBeNull().ShouldContain($"[{saved.Value.Note.Id}] WebFetch can't read github.com; use gh. (27 Sep 2026)",
            customMessage: "machine notes are in the one file every folder reads");
        _store.Context[_repository].ShouldNotContain("WebFetch");
        var broadcast = _broadcaster.Broadcasts.Single();
        broadcast.Topic.ShouldBe("sessions");
        broadcast.Type.ShouldBe(AgentMemory.SavedEventType);
        broadcast.UserId.ShouldBe("owner");
        broadcast.Payload.GetProperty("note").GetProperty("text").GetString().ShouldBe("WebFetch can't read github.com; use gh.");
    }

    [Fact]
    public async Task Repository_notes_stay_with_their_repository_and_a_worktree_shares_its_checkouts_notes()
    {
        await _memory.SetEnabledAsync(true);

        await _memory.SaveFromAgentAsync(Session(_worktree), "repository", "Run E2E with --filter; the full suite passes the 10-minute limit.", "learned", null);

        (await _memory.ListAsync(_repository)).RepositoryNotes.Single().Repository.ShouldBe(_repository);
        (await _memory.ListAsync(Path.Combine(_repository, "src"))).RepositoryNotes.Count.ShouldBe(1);
        (await _memory.ListAsync(_otherRepository)).RepositoryNotes.ShouldBeEmpty();
        var other = await _memory.PrepareSessionAsync("owner", _otherRepository, canSave: false);
        other.ShouldNotBeNull();
        other.ShouldNotContain("--filter");
    }

    [Fact]
    public async Task Saying_a_note_again_confirms_it_instead_of_adding_another()
    {
        await _memory.SetEnabledAsync(true);
        var first = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "Use gh for GitHub.", "learned", null);
        _time.Advance(TimeSpan.FromDays(3));

        var again = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "use gh for github.", "learned", null);

        again.Value.AlreadyKnown.ShouldBeTrue();
        again.Value.Note.Id.ShouldBe(first.Value.Note.Id);
        again.Value.Note.Updated.ShouldBe(_time.GetUtcNow());
        _store.Notes.Count.ShouldBe(1);
        _broadcaster.Broadcasts.Count.ShouldBe(1, "confirming a note isn't news for the user");
    }

    [Fact]
    public async Task Replacing_a_note_corrects_it_in_place()
    {
        await _memory.SetEnabledAsync(true);
        var wrong = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "Client tests run with bun run test:unit.", "learned", null);

        var corrected = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "Client tests run with bun run test.", "learned", wrong.Value.Note.Id);

        corrected.Value.Note.Id.ShouldBe(wrong.Value.Note.Id);
        corrected.Value.Replaced.ShouldBe(wrong.Value.Note.Id);
        _store.Notes.Single().Text.ShouldBe("Client tests run with bun run test.");
    }

    [Fact]
    public async Task An_agent_cant_replace_or_forget_another_repositorys_note()
    {
        await _memory.SetEnabledAsync(true);
        var elsewhere = await _memory.SaveFromAgentAsync(Session(_otherRepository), "repository", "Only in weave-cli.", "learned", null);

        var replaced = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "Changed.", "learned", elsewhere.Value.Note.Id);
        var forgotten = await _memory.ForgetFromAgentAsync(Session(_repository), elsewhere.Value.Note.Id);

        replaced.IsFailure.ShouldBeTrue();
        forgotten.IsFailure.ShouldBeTrue();
        _store.Notes.Single().Text.ShouldBe("Only in weave-cli.");
    }

    [Fact]
    public async Task A_full_list_makes_the_agent_forget_or_merge_first()
    {
        await _memory.SetEnabledAsync(true);
        for (var i = 0; i < AgentMemory.MaxMachineNotes; i++)
            (await _memory.SaveFromAgentAsync(Session(_repository), "machine", $"Machine fact {i}.", "learned", null)).IsSuccess.ShouldBeTrue();

        var refused = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "One too many.", "learned", null);
        var merged = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "Facts 0 and 1, merged.", "learned", _store.Notes[0].Id);

        refused.IsFailure.ShouldBeTrue();
        refused.Error.Description.ShouldContain("full");
        merged.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, "Note.")]
    [InlineData("desk", "Note.")]
    [InlineData("machine", "")]
    public async Task Invalid_notes_are_refused(string? list, string text)
    {
        await _memory.SetEnabledAsync(true);

        (await _memory.SaveFromAgentAsync(Session(_repository), list, text, "learned", null)).IsFailure.ShouldBeTrue();
        _store.Notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_note_longer_than_the_limit_is_refused()
    {
        await _memory.SetEnabledAsync(true);

        var result = await _memory.SaveFromAgentAsync(Session(_repository), "machine", new string('x', AgentMemory.MaxNoteLength + 1), "learned", null);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task The_user_can_add_edit_forget_and_clear_notes()
    {
        await _memory.SetEnabledAsync(true);
        var repositoryNote = await _memory.AddAsync("repository", Path.Combine(_repository, "client"), "Rebase, don't merge main.");
        await _memory.AddAsync("machine", null, "7 GB of memory: one dotnet app at a time.");
        await _memory.AddAsync("repository", _otherRepository, "Only here.");

        repositoryNote.Value.Kind.ShouldBe(MemoryKinds.Added);
        repositoryNote.Value.Repository.ShouldBe(_repository);
        (await _memory.UpdateAsync(repositoryNote.Value.Id, "Rebase onto origin/main.")).Value.Text.ShouldBe("Rebase onto origin/main.");

        (await _memory.ClearAsync("repository", _repository)).Value.ShouldBe(1);
        (await _memory.ClearAsync("machine", null)).Value.ShouldBe(1);
        _store.Notes.Single().Text.ShouldBe("Only here.");
        (await _memory.ClearAsync("all", null)).Value.ShouldBe(1);
        _store.Notes.ShouldBeEmpty();
        (await _memory.ClearAsync("everything", null)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task The_overview_counts_notes_per_repository()
    {
        await _memory.AddAsync("repository", _repository, "One.");
        await _memory.AddAsync("repository", _repository, "Two.");
        await _memory.AddAsync("machine", null, "Three.");

        var overview = await _memory.GetOverviewAsync();

        overview.Enabled.ShouldBeFalse();
        overview.Repositories.ShouldBe([new MemoryRepositoryView(_repository, "weave-fleet", 2)]);
        overview.MachineCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_repository_note_rewrites_only_that_repositorys_folders_and_a_machine_note_only_the_machine_file()
    {
        await _memory.SetEnabledAsync(true);
        await _memory.PrepareSessionAsync("owner", _repository, canSave: false);
        await _memory.PrepareSessionAsync("owner", _worktree, canSave: false);
        await _memory.PrepareSessionAsync("owner", _otherRepository, canSave: false);
        _store.Writes.Clear();

        await _memory.SaveFromAgentAsync(Session(_worktree), "repository", "Only for weave-fleet.", "learned", null);

        _store.Writes.ShouldBe([_repository, _worktree], ignoreOrder: true);
        _store.Context[_worktree].ShouldContain("Only for weave-fleet.");
        _store.Writes.Clear();

        var machineWrites = _store.MachineWrites;
        await _memory.SaveFromAgentAsync(Session(_repository), "machine", "For every repository.", "learned", null);

        _store.Writes.ShouldBeEmpty();
        _store.MachineWrites.ShouldBe(machineWrites + 1);
        _store.Machine.ShouldNotBeNull().ShouldContain("For every repository.");
    }

    [Fact]
    public async Task A_session_folder_that_no_longer_exists_is_forgotten()
    {
        await _memory.SetEnabledAsync(true);
        var gone = Path.Combine(_repository, "src", "gone");
        Directory.CreateDirectory(gone);
        await _memory.PrepareSessionAsync("owner", gone, canSave: false);
        Directory.Delete(gone);

        await _memory.AddAsync("machine", null, "Anything.");

        _store.Context.Keys.ShouldNotContain(gone);
    }

    [Fact]
    public async Task A_running_session_starts_with_its_notes_and_hears_about_changes_made_elsewhere_once()
    {
        await _memory.SetEnabledAsync(true);
        var gh = (await _memory.AddAsync("machine", null, "Use gh for GitHub.")).Value;
        var session = Session(_repository);

        (await _memory.ChangesForAsync(session)).ShouldBeNull("its instructions already hold the notes on the first prompt");

        await _memory.UpdateAsync(gh.Id, "Use gh for GitHub; WebFetch can't read it.");
        var e2e = (await _memory.AddAsync("repository", _repository, "Run E2E with --filter.")).Value;
        await _memory.AddAsync("repository", _otherRepository, "Only for weave-cli.");
        var changes = await _memory.ChangesForAsync(session);

        changes.ShouldNotBeNull();
        changes.ShouldStartWith("# Fleet memory changed");
        changes.ShouldContain($"[{gh.Id}] Use gh for GitHub; WebFetch can't read it. (27 Sep 2026)");
        changes.ShouldContain($"[{e2e.Id}] Run E2E with --filter. (27 Sep 2026)");
        changes.ShouldNotContain("weave-cli", customMessage: "another repository's notes aren't this session's");
        (await _memory.ChangesForAsync(session)).ShouldBeNull("each change is told once");
    }

    [Fact]
    public async Task A_note_forgotten_elsewhere_is_named_as_no_longer_true()
    {
        await _memory.SetEnabledAsync(true);
        var gh = (await _memory.AddAsync("machine", null, "Use gh for GitHub.")).Value;
        var session = Session(_repository);
        await _memory.ChangesForAsync(session);

        await _memory.ForgetAsync(gh.Id);

        (await _memory.ChangesForAsync(session)).ShouldNotBeNull().ShouldContain($"Forgotten, so no longer true: [{gh.Id}].");
    }

    [Fact]
    public async Task What_a_sessions_own_agent_saves_or_forgets_isnt_told_back_to_it_but_other_sessions_hear_of_it()
    {
        await _memory.SetEnabledAsync(true);
        var session = Session(_repository);
        var other = Session(_repository, "session-2");
        await _memory.ChangesForAsync(session);
        await _memory.ChangesForAsync(other);

        var saved = (await _memory.SaveFromAgentAsync(session, "machine", "Use gh for GitHub.", "learned", null)).Value.Note;
        var corrected = (await _memory.SaveFromAgentAsync(session, "machine", "Use gh for GitHub and its API.", "learned", saved.Id)).Value.Note;

        (await _memory.ChangesForAsync(session)).ShouldBeNull("the agent saved it, so it knows");
        (await _memory.ChangesForAsync(other)).ShouldNotBeNull().ShouldContain($"[{corrected.Id}] Use gh for GitHub and its API.");

        await _memory.ForgetFromAgentAsync(session, corrected.Id);

        (await _memory.ChangesForAsync(session)).ShouldBeNull("the agent forgot it, so it knows");
        (await _memory.ChangesForAsync(other)).ShouldNotBeNull().ShouldContain($"[{corrected.Id}]");
    }

    [Fact]
    public async Task A_running_session_is_told_once_when_memory_is_turned_off()
    {
        await _memory.SetEnabledAsync(true);
        await _memory.AddAsync("machine", null, "Use gh for GitHub.");
        var session = Session(_repository);
        await _memory.ChangesForAsync(session);

        await _memory.SetEnabledAsync(false);

        (await _memory.ChangesForAsync(session)).ShouldBe(AgentMemoryPrompt.TurnedOff);
        (await _memory.ChangesForAsync(session)).ShouldBeNull();
        (await _memory.ChangesForAsync(Session(_repository, "never-had-notes"))).ShouldBeNull();
    }

    private static Session Session(string directory, string id = "session-1") => new()
    {
        Id = id,
        Title = "Fix the flaky test",
        Directory = directory,
        UserId = "owner",
    };

    private string MakeRepository(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        Directory.CreateDirectory(Path.Combine(path, "src"));
        return path;
    }
}
