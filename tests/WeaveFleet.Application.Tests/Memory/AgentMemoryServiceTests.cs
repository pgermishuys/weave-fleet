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
    private readonly AgentMemoryService _memory;
    private readonly string _repository;
    private readonly string _worktree;
    private readonly string _otherRepository;

    public AgentMemoryServiceTests()
    {
        _memory = new AgentMemoryService(_store, _preferences, new TestUserContext("owner"), _time, _broadcaster);
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
        notes.ShouldContain("## This machine");
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
        _store.Context[_repository].ShouldContain($"[{saved.Value.Note.Id}] WebFetch can't read github.com; use gh. (27 Sep 2026)");
        _store.Context[_otherRepository].ShouldContain("WebFetch can't read github.com", customMessage: "machine notes reach every repository");
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

    private static Session Session(string directory) => new()
    {
        Id = "session-1",
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

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<MemoryNote> Notes { get; } = [];
        public Dictionary<string, string> Context { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryNote>>([.. Notes]);

        public Task SaveAsync(string userId, MemoryNote note, CancellationToken ct = default)
        {
            var index = Notes.FindIndex(existing => existing.Id == note.Id);
            if (index >= 0)
                Notes[index] = note;
            else
                Notes.Add(note);
            return Task.CompletedTask;
        }

        public Task<int> DeleteAsync(string userId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
            => Task.FromResult(Notes.RemoveAll(note => ids.Contains(note.Id)));

        public string ContextFolder(string userId) => "/memory/context";

        public Task WriteContextAsync(string userId, string directory, string content, CancellationToken ct = default)
        {
            Context[directory] = content;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListContextDirectoriesAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([.. Context.Keys]);

        public Task ClearContextAsync(string userId, CancellationToken ct = default)
        {
            Context.Clear();
            return Task.CompletedTask;
        }
    }
}
