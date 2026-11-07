using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Memory;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

/// <summary>"Search older notes": the short list every session reads, the rest found by search.</summary>
public sealed class MemorySearchTierTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-memory-tiers-").FullName;
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeMemoryStore _store = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly AgentMemorySessions _told = new();
    private readonly AgentMemoryService _memory;
    private readonly string _repository;

    public MemorySearchTierTests()
    {
        _memory = new AgentMemoryService(_store, _preferences, new TestUserContext("owner"), _time, new FakeEventBroadcaster(), told: _told);
        _repository = Path.Combine(_root, "weave-fleet");
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task TurnOnAsync(bool search = true)
    {
        await _memory.SetEnabledAsync(true);
        if (search)
            await _preferences.SetAsync(AgentMemory.SearchPreferenceKey, "true");
    }

    private MemoryNote Note(string id, string text, string kind = MemoryKinds.Learned, int minutesOld = 0, bool inSearch = false, bool pinned = false, int found = 0, MemoryList list = MemoryList.Repository, string? how = null)
        => new(id, list, text, kind, list == MemoryList.Repository ? _repository : null, null, null, Start.AddMinutes(-minutesOld), Start.AddMinutes(-minutesOld),
            kind == MemoryKinds.Learned ? 30 : null, 0, how, inSearch, pinned, found);

    private async Task SeedAsync(int always, int search = 0, string kind = MemoryKinds.Learned)
    {
        for (var i = 0; i < always; i++)
            await _store.SaveAsync("owner", Note($"a{i:000}", $"always note {i} about tests", kind, minutesOld: 100 - i));
        for (var i = 0; i < search; i++)
            await _store.SaveAsync("owner", Note($"s{i:000}", $"search note {i} about releases", kind, minutesOld: 500 - i, inSearch: true));
    }

    private static Session Session(string directory, string id = "session-1") => new() { Id = id, Title = "Work", Directory = directory, UserId = "owner" };

    // ---- the pure rules ----

    [Fact]
    public void A_note_without_a_placement_is_read_by_every_session_and_a_pinned_one_always_is()
    {
        MemoryTiers.IsAlways(Note("a", "x")).ShouldBeTrue();
        MemoryTiers.IsAlways(Note("b", "x", inSearch: true)).ShouldBeFalse();
        MemoryTiers.IsAlways(Note("c", "x", inSearch: true, pinned: true)).ShouldBeTrue();
    }

    [Fact]
    public void The_note_that_moves_down_is_learned_before_from_you_never_pinned_least_found_then_oldest()
    {
        var notes = new[]
        {
            Note("pin", "pinned learned", minutesOld: 900, pinned: true),
            Note("you", "said by the user", MemoryKinds.FromYou, minutesOld: 800),
            Note("used", "learned but found often", minutesOld: 700, found: 5),
            Note("new", "learned, found twice", minutesOld: 10, found: 2),
            Note("old", "learned, found twice, older", minutesOld: 50, found: 2),
        };

        var order = new List<string>();
        var rest = notes.ToList();
        while (MemoryTiers.PickToMoveDown(rest) is { } next)
        {
            order.Add(next.Id);
            rest.Remove(next);
        }

        order.ShouldBe(["old", "new", "used", "you"]);
        rest.Single().Id.ShouldBe("pin");
    }

    [Fact]
    public void With_search_off_a_long_list_is_read_up_to_the_old_cap_always_read_notes_first_then_the_most_found()
    {
        var notes = Enumerable.Range(0, 30).Select(i => Note($"a{i:00}", $"always {i}", minutesOld: i)).ToList();
        notes.AddRange(Enumerable.Range(0, 20).Select(i => Note($"s{i:00}", $"search {i}", minutesOld: i, inSearch: true, found: i)));

        var (read, searchable) = MemoryTiers.Choose(notes, MemoryList.Repository, searchOn: false);

        searchable.ShouldBe(0);
        read.Count.ShouldBe(AgentMemory.MaxRepositoryNotes);
        read.Count(note => note.Id.StartsWith('a')).ShouldBe(30);
        read.Where(note => note.Id.StartsWith('s')).Select(note => note.Id).ShouldBe(["s19", "s18", "s17", "s16", "s15", "s14", "s13", "s12", "s11", "s10"]);
        MemoryTiers.Choose(notes.Take(10).ToList(), MemoryList.Repository, searchOn: false).Read.Count.ShouldBe(10);
    }

    [Fact]
    public void Search_ranks_by_the_words_a_note_has_with_light_stemming_and_counts_how_before_ties()
    {
        var notes = new[]
        {
            Note("one", "Cut a release by tagging main after the changelog is merged."),
            Note("two", "Tests time out on CI when the runner is busy.", how: "saw the release workflow fail twice"),
            Note("three", "Releases are published from the tag."),
            Note("four", "Run dotnet test with a scratch HOME."),
        };

        MemoryTiers.Search(notes, "releasing a release", 8).Select(note => note.Id).ShouldBe(["one", "three", "two"]);
        MemoryTiers.Search(notes, "the and of", 8).ShouldBeEmpty("filler words alone match nothing");
        MemoryTiers.Search(notes, "release", 1).Single().Id.ShouldBe("one");
    }

    // ---- the prompt ----

    [Fact]
    public async Task With_search_on_a_session_reads_only_the_always_read_notes_and_is_told_how_many_more_can_be_searched()
    {
        await TurnOnAsync();
        await SeedAsync(always: 15, search: 10);

        var prompt = await _memory.PrepareSessionAsync("owner", _repository, canSave: true);

        prompt.ShouldNotBeNull();
        prompt.Split('\n').Count(line => line.StartsWith("- [a", StringComparison.Ordinal)).ShouldBe(15);
        prompt.ShouldNotContain("- [s0");
        prompt.ShouldContain("10 more notes about this repository are kept for search: call `fleet_memory_search` when you start on an area you haven't worked on in this session, or before a risky step.");
        _store.Context[_repository].ShouldContain("10 more notes");
        prompt.ShouldContain("When the list kept for search is full, forget or merge an old note first.");
    }

    [Fact]
    public async Task With_search_off_the_prompt_is_todays_even_when_notes_sit_in_search()
    {
        await TurnOnAsync(search: false);
        await SeedAsync(always: 3, search: 2);

        var prompt = await _memory.PrepareSessionAsync("owner", _repository, canSave: true);

        prompt.ShouldNotBeNull();
        prompt.ShouldNotContain("fleet_memory_search");
        prompt.ShouldNotContain("kept for search");
        prompt.ShouldContain("When a list is full, forget or merge an old note first.");
        prompt.Split('\n').Count(line => line.StartsWith("- [", StringComparison.Ordinal)).ShouldBe(5, "search off: nothing is hidden below the old cap");
    }

    [Fact]
    public void The_prompt_without_the_switch_is_pinned_byte_for_byte()
    {
        var notes = new[] { new MemoryNote("abcd1234", MemoryList.Repository, "Run tests with TMPDIR set.", MemoryKinds.FromYou, "/r/weave-fleet", null, null, Start, Start) };

        var text = AgentMemoryPrompt.RenderFolder("/r/weave-fleet", notes) + AgentMemoryPrompt.RenderMachine([]);

        text.ShouldEndWith(
            "Never save a note that repeats one below. When a list is full, forget or merge an old note first.\n\n"
            + "## This repository (weave-fleet)\n- [abcd1234] Run tests with TMPDIR set. (7 Nov 2026)\n\n## This machine\nNo notes yet.\n");
    }

    [Fact]
    public async Task Turning_search_on_with_a_long_list_moves_the_least_used_notes_down_the_first_time_a_session_starts()
    {
        await TurnOnAsync(search: false);
        await SeedAsync(always: 20);
        await _preferences.SetAsync(AgentMemory.SearchPreferenceKey, "true");

        var prompt = await _memory.PrepareSessionAsync("owner", _repository, canSave: true);

        prompt!.Split('\n').Count(line => line.StartsWith("- [a", StringComparison.Ordinal)).ShouldBe(15);
        prompt.ShouldContain("5 more notes about this repository");
        (await _store.ListAsync("owner")).Count(note => note.InSearch).ShouldBe(5);
        (await _store.ListAsync("owner")).Count.ShouldBe(20, "nothing is deleted");
    }

    [Fact]
    public async Task Turning_search_off_again_moves_nothing_and_a_session_reads_up_to_the_old_caps()
    {
        await TurnOnAsync();
        await SeedAsync(always: 15, search: 30);
        await _preferences.SetAsync(AgentMemory.SearchPreferenceKey, "false");

        var prompt = await _memory.PrepareSessionAsync("owner", _repository, canSave: true);

        prompt!.Split('\n').Count(line => line.StartsWith("- [", StringComparison.Ordinal)).ShouldBe(AgentMemory.MaxRepositoryNotes);
        prompt.Split('\n').Count(line => line.StartsWith("- [a", StringComparison.Ordinal)).ShouldBe(15, "always-read notes first");
        (await _store.ListAsync("owner")).Count(note => note.InSearch).ShouldBe(30, "none moved");
    }

    // ---- saving ----

    [Fact]
    public async Task A_new_note_goes_in_always_read_and_when_that_is_full_the_least_used_learned_note_moves_to_search()
    {
        await TurnOnAsync();
        await SeedAsync(always: 13);
        await _store.SaveAsync("owner", Note("you1", "the user said so", MemoryKinds.FromYou, minutesOld: 9000));
        await _store.SaveAsync("owner", Note("pin1", "pinned learned", minutesOld: 8000, pinned: true));

        var saved = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "A brand new fact.", "learned", null);

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Note.InSearch.ShouldBeFalse();
        var moved = saved.Value.MovedDown.ShouldNotBeNull().Single();
        moved.Id.ShouldBe("a000", "the oldest learned note nobody found");
        var all = await _store.ListAsync("owner");
        all.Single(note => note.Id == "a000").InSearch.ShouldBeTrue();
        all.Single(note => note.Id == "you1").InSearch.ShouldBeFalse();
        all.Single(note => note.Id == "pin1").InSearch.ShouldBeFalse();
        all.Count(MemoryTiers.IsAlways).ShouldBe(AgentMemory.MaxAlwaysRepositoryNotes);
    }

    [Fact]
    public async Task A_save_is_refused_only_when_the_search_group_is_full_or_every_always_read_note_is_pinned()
    {
        await TurnOnAsync();
        await SeedAsync(always: 15, search: AgentMemory.MaxSearchRepositoryNotes);

        var full = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "One too many.", "learned", null);
        full.IsFailure.ShouldBeTrue();
        full.Error.Description.ShouldContain("kept for search");

        await _memory.ForgetFromAgentAsync(Session(_repository), "s000");
        (await _memory.SaveFromAgentAsync(Session(_repository), "repository", "Room now.", "learned", null)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Every_always_read_note_pinned_leaves_no_room_to_move()
    {
        await TurnOnAsync();
        for (var i = 0; i < 15; i++)
            await _store.SaveAsync("owner", Note($"p{i:00}", $"pinned {i}", pinned: true));

        var result = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "No room.", "learned", null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("pinned");
    }

    [Fact]
    public async Task With_search_off_the_old_cap_still_refuses_a_forty_first_note()
    {
        await TurnOnAsync(search: false);
        await SeedAsync(always: 40);

        var result = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "Forty-one.", "learned", null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("list is full (40 notes)");
    }

    [Fact]
    public async Task The_machine_list_has_its_own_smaller_caps()
    {
        await TurnOnAsync();
        for (var i = 0; i < 8; i++)
            await _store.SaveAsync("owner", Note($"m{i:00}", $"machine {i}", list: MemoryList.Machine, minutesOld: 50 - i));

        var saved = await _memory.SaveFromAgentAsync(Session(_repository), "machine", "A ninth machine note.", "learned", null);

        saved.Value.MovedDown.ShouldNotBeNull().Single().Id.ShouldBe("m00");
    }

    [Fact]
    public async Task A_note_replacing_another_takes_its_place_and_its_find_count()
    {
        await TurnOnAsync();
        await _store.SaveAsync("owner", Note("old1", "old fact", inSearch: true, found: 4));

        var saved = await _memory.SaveFromAgentAsync(Session(_repository), "repository", "corrected fact", "learned", "old1");

        saved.Value.MovedDown.ShouldBeEmpty();
        saved.Value.Note.InSearch.ShouldBeTrue("it replaces a note that was found by search");
        saved.Value.Note.Found.ShouldBe(4);
    }

    // ---- searching ----

    [Fact]
    public async Task A_search_finds_notes_from_both_groups_counts_the_finds_and_skips_the_expired()
    {
        await TurnOnAsync();
        await _store.SaveAsync("owner", Note("rel1", "Cut a release by tagging main.", inSearch: true));
        await _store.SaveAsync("owner", Note("rel2", "Release notes live in the changelog.", MemoryKinds.FromYou));
        await _store.SaveAsync("owner", Note("oth1", "Tests need a scratch HOME."));
        await _store.SaveAsync("owner", Note("mac1", "The release box has 14 GB.", list: MemoryList.Machine, inSearch: true));

        var found = await _memory.SearchFromAgentAsync(Session(_repository), "release", "repository");

        found.IsSuccess.ShouldBeTrue();
        found.Value.Notes.Select(note => note.Id).Order().ShouldBe(["rel1", "rel2"]);
        found.Value.Searched.ShouldBe(3);
        (await _store.ListAsync("owner")).Where(note => note.Id.StartsWith("rel", StringComparison.Ordinal)).ShouldAllBe(note => note.Found == 1 && note.LastFound == Start);

        (await _memory.SearchFromAgentAsync(Session(_repository), "release", "both")).Value.Notes.Count.ShouldBe(3);
        (await _store.ListAsync("owner")).Single(note => note.Id == "rel1").Found.ShouldBe(2);
        (await _store.ListAsync("owner")).Single(note => note.Id == "oth1").Found.ShouldBe(0, "a note that isn't returned isn't counted");

        _time.Advance(TimeSpan.FromDays(40));
        for (var day = 1; day <= 35; day++)
            await _store.RecordDayUsedAsync("owner", _repository, AgentMemory.Day(Start.AddDays(day), _time.LocalTimeZone));
        (await _memory.SearchFromAgentAsync(Session(_repository), "release", "repository")).Value.Notes.Select(note => note.Id).ShouldBe(["rel2"], "the learned notes expired");
    }

    [Fact]
    public async Task A_search_needs_a_query_and_a_known_list_and_returns_at_most_eight()
    {
        await TurnOnAsync();
        (await _memory.SearchFromAgentAsync(Session(_repository), " ", "both")).IsFailure.ShouldBeTrue();
        (await _memory.SearchFromAgentAsync(Session(_repository), "x", "everywhere")).IsFailure.ShouldBeTrue();
        for (var i = 0; i < 12; i++)
            await _store.SaveAsync("owner", Note($"r{i:00}", $"release step {i}", inSearch: true));

        (await _memory.SearchFromAgentAsync(Session(_repository), "release", "both")).Value.Notes.Count.ShouldBe(AgentMemory.MaxSearchResults);
    }

    // ---- sessions already running ----

    [Fact]
    public async Task A_note_moved_down_mid_session_needs_no_message_and_one_moved_up_arrives_as_changed()
    {
        await TurnOnAsync();
        await SeedAsync(always: 3, search: 1);
        var session = Session(_repository);
        (await _memory.ChangesForAsync(session)).ShouldBeNull("the first prompt");

        await _store.SaveAsync("owner", Note("a000", "always note 0 about tests", inSearch: true));
        (await _memory.ChangesForAsync(session)).ShouldBeNull("moved down: dropped without a word");

        await _memory.MoveAsync("s000", toSearch: false);
        var changes = await _memory.ChangesForAsync(session);
        changes.ShouldNotBeNull().ShouldContain("[s000]");
    }

    // ---- Settings ----

    [Fact]
    public async Task Pinning_a_note_to_always_read_makes_room_by_moving_the_least_used_down_and_moving_to_search_unpins()
    {
        await TurnOnAsync();
        await SeedAsync(always: 15, search: 2);

        var pinned = await _memory.MoveAsync("s000", toSearch: false);

        pinned.IsSuccess.ShouldBeTrue();
        pinned.Value.Pinned.ShouldBeTrue();
        pinned.Value.InSearch.ShouldBeFalse();
        var all = await _store.ListAsync("owner");
        all.Count(MemoryTiers.IsAlways).ShouldBe(15);
        all.Single(note => note.Id == "a000").InSearch.ShouldBeTrue();

        var down = await _memory.MoveAsync("s000", toSearch: true);
        down.Value.Pinned.ShouldBeFalse();
        down.Value.InSearch.ShouldBeTrue();
        (await _memory.MoveAsync("nope", toSearch: true)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task The_notes_view_estimates_tokens_for_the_always_read_group_only()
    {
        await TurnOnAsync();
        await SeedAsync(always: 5, search: 40);
        var on = await _memory.ListAsync(_repository);

        await _preferences.SetAsync(AgentMemory.SearchPreferenceKey, "false");
        var off = await _memory.ListAsync(_repository);

        on.RepositoryNotes.Count.ShouldBe(45);
        on.RepositoryNotes.Count(note => note.InSearch).ShouldBe(40);
        on.Tokens.ShouldBeLessThan(off.Tokens);
        var read = (await _store.ListAsync("owner")).Where(MemoryTiers.IsAlways).ToList();
        on.Tokens.ShouldBe((AgentMemoryPrompt.Render(_repository, read, [], true, 40, 0).Length + 3) / 4, "what a request carries: the rules, the short list and the search line");
        off.Tokens.ShouldBe((AgentMemoryPrompt.Render(_repository, MemoryTiers.Choose((await _store.ListAsync("owner")).ToList(), MemoryList.Repository, searchOn: false).Read, [], true).Length + 3) / 4, "search off: today's notes, up to the old cap of 40");
        (await _memory.GetOverviewAsync()).SearchEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task The_search_switch_alone_does_nothing_while_memory_is_off()
    {
        await _preferences.SetAsync(AgentMemory.SearchPreferenceKey, "true");

        (await _memory.IsSearchOnAsync()).ShouldBeFalse();
        (await _memory.PrepareSessionAsync("owner", _repository, canSave: true)).ShouldBeNull();
    }
}
