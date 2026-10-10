using System.Text.Json;
using WeaveFleet.Application.Mods;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModServiceTests
{
    private const string User = "test-user";
    private const string Chips = "test-chips";
    private const string SessionId = "session-1";

    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeModChecker _checker = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ModsSafeMode _safeMode = new();
    private readonly ModService _service;

    public ModServiceTests()
    {
        _sessions.Seed(new Session { Id = SessionId, Title = "Tidy the build output" });
        _service = new ModService(_store, _checker, _events, new TestUserContext(User), _safeMode, _sessions, TimeProvider.System);
    }

    private static ModVersion Version(int number, string? note = null)
        => new(number, new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), $"0.{number}.0", "sha", SessionId, "Tidy the build output", note, null);

    /// <summary>Chips with versions 1..<paramref name="count"/>, the last active.</summary>
    private void SeedChips(int count, int? active = null, ModOff? off = null)
    {
        for (var i = 1; i <= count; i++)
            _store.SeedVersion(User, Chips, i, $"0.{i}.0", $"Shows chips v{i}");
        _store.SeedHistory(User, new ModHistory(Chips, active ?? count, off, Enumerable.Range(1, count).Select(i => Version(i)).ToList()));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private (string Reason, string? Name, string? SessionId) ChangedOnce()
    {
        var sent = _events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe(("sessions", "mods.changed", User));
        var payload = sent.DomainEvent.ShouldBeOfType<ModsChanged>().Payload;
        sent.Payload.GetProperty("reason").GetString().ShouldBe(payload.Reason);
        return (payload.Reason, payload.Name, payload.SessionId);
    }

    // ── Reading ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_shows_each_mod_with_the_active_versions_description_and_version()
    {
        SeedChips(count: 2, active: 1);

        var view = await _service.ListAsync();

        view.SafeMode.ShouldBeFalse();
        var mod = view.Mods.ShouldHaveSingleItem();
        (mod.Name, mod.Description, mod.Active, mod.ActiveVersion, mod.Off).ShouldBe((Chips, "Shows chips v1", 1, "0.1.0", null));
        mod.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_reports_safe_mode()
    {
        _safeMode.Set(true);

        (await _service.ListAsync()).SafeMode.ShouldBeTrue();
    }

    [Fact]
    public async Task Get_returns_the_mod()
    {
        SeedChips(count: 1);

        (await _service.GetAsync(Chips)).Value.Name.ShouldBe(Chips);
    }

    [Theory]
    [InlineData("nothing-kept")]
    [InlineData("../escape")]
    [InlineData("Chips")]
    [InlineData("fleet-own")]
    [InlineData("drafts")]
    public async Task Get_is_not_found_for_a_mod_with_no_versions_or_an_invalid_name(string name)
    {
        var result = await _service.GetAsync(name);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task A_versions_files_are_read_from_the_store()
    {
        SeedChips(count: 2);

        var files = (await _service.ReadVersionFilesAsync(Chips, 2)).Value;

        files.Files.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts"]);
        files.Files[1].Content.ShouldBe("// v2");
    }

    [Fact]
    public async Task Files_of_a_version_that_isnt_there_are_not_found()
    {
        SeedChips(count: 1);

        (await _service.ReadVersionFilesAsync(Chips, 9)).Error.Code.ShouldEndWith(".NotFound");
        (await _service.ReadVersionFilesAsync("../x", 1)).Error.Code.ShouldEndWith(".NotFound");
    }

    // ── Use a version, turn on and off ──────────────────────────────────

    [Fact]
    public async Task Using_a_version_makes_it_active_turns_the_mod_on_and_says_so_once()
    {
        SeedChips(count: 3, off: new ModOff(ModOffBy.User, DateTimeOffset.UnixEpoch));

        var mod = (await _service.UseVersionAsync(Chips, 1)).Value;

        (mod.Active, mod.ActiveVersion, mod.Off).ShouldBe((1, "0.1.0", null));
        ChangedOnce().ShouldBe(("version", Chips, null));
    }

    [Fact]
    public async Task Using_a_version_that_doesnt_exist_changes_and_raises_nothing()
    {
        SeedChips(count: 2);

        var result = await _service.UseVersionAsync(Chips, 7);

        result.IsFailure.ShouldBeTrue();
        _store.Writes.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_a_mod_off_and_on_raises_off_then_on()
    {
        SeedChips(count: 1);

        var off = (await _service.SetOnAsync(Chips, on: false)).Value;
        off.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        _events.Broadcasts.Single().DomainEvent.ShouldBeOfType<ModsChanged>().Payload.Reason.ShouldBe("off");

        var on = (await _service.SetOnAsync(Chips, on: true)).Value;
        on.Off.ShouldBeNull();
        _events.Broadcasts.Select(b => ((ModsChanged)b.DomainEvent!).Payload.Reason).ShouldBe(["off", "on"]);
    }

    [Fact]
    public async Task Turning_off_a_mod_that_isnt_kept_is_not_found_and_raises_nothing()
    {
        (await _service.SetOnAsync(Chips, on: false)).Error.Code.ShouldEndWith(".NotFound");

        _events.Broadcasts.ShouldBeEmpty();
        _store.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Three_strikes_turn_the_mod_off_with_the_error()
    {
        SeedChips(count: 1);

        var mod = (await _service.RecordStrikesAsync(Chips, "TypeError: x is undefined")).Value;

        var off = mod.Off.ShouldNotBeNull();
        (off.By, off.Error).ShouldBe((ModOffBy.Strikes, "TypeError: x is undefined"));
        ChangedOnce().ShouldBe(("strikes", Chips, null));
    }

    // ── Undo ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Undo_walks_back_one_version_at_a_time_then_turns_the_mod_off_then_has_nothing_to_undo()
    {
        SeedChips(count: 3);

        var v2 = (await _service.UndoAsync(Chips)).Value;
        (v2.Active, v2.Off).ShouldBe((2, null));

        var v1 = (await _service.UndoAsync(Chips)).Value;
        (v1.Active, v1.Off).ShouldBe((1, null));

        var off = (await _service.UndoAsync(Chips)).Value;
        off.Active.ShouldBe(1);
        off.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);

        var again = await _service.UndoAsync(Chips);
        again.IsFailure.ShouldBeTrue();
        again.Error.Description.ShouldBe("Nothing to undo");

        _events.Broadcasts.Select(b => ((ModsChanged)b.DomainEvent!).Payload.Reason).ShouldBe(["undone", "undone", "undone"]);
        (await _store.GetAsync(User, Chips)).Versions.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Undo_skips_numbers_that_were_never_kept()
    {
        _store.SeedVersion(User, Chips, 1);
        _store.SeedVersion(User, Chips, 4);
        _store.SeedHistory(User, new ModHistory(Chips, 4, null, [Version(1), Version(4)]));

        (await _service.UndoAsync(Chips)).Value.Active.ShouldBe(1);
    }

    [Fact]
    public async Task Undo_of_an_unknown_mod_is_not_found()
    {
        (await _service.UndoAsync(Chips)).Error.Code.ShouldEndWith(".NotFound");
        _events.Broadcasts.ShouldBeEmpty();
    }

    // ── Drafts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Drafts_list_with_their_description_version_and_the_kept_mod_of_the_same_name()
    {
        SeedChips(count: 2);
        _store.SeedDraft(User, SessionId, Chips);
        _store.SeedDraft(User, SessionId, "other-mod", withManifest: false);

        var drafts = (await _service.ListDraftsAsync(SessionId)).Value;

        drafts.Select(d => d.Name).ShouldBe([Chips, "other-mod"]);
        var chips = drafts[0];
        (chips.SessionId, chips.Description, chips.Version, chips.Kept, chips.Off).ShouldBe((SessionId, "Shows chips", "0.1.0", 2, null));
        var other = drafts[1];
        (other.Description, other.Version, other.Kept).ShouldBe((null, null, null));
    }

    [Fact]
    public async Task Draft_files_are_read_from_the_store_and_a_missing_draft_is_not_found()
    {
        _store.SeedDraft(User, SessionId, Chips);

        (await _service.ReadDraftFilesAsync(SessionId, Chips)).Value.Files.Count.ShouldBe(2);
        (await _service.ReadDraftFilesAsync(SessionId, "nope")).Error.Code.ShouldEndWith(".NotFound");
        (await _service.ReadDraftFilesAsync("../x", Chips)).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task Checking_a_draft_runs_the_checker_on_its_folder()
    {
        _store.SeedDraft(User, SessionId, Chips);
        _checker.Report = Json("""{"ok":true}""");

        var check = (await _service.CheckDraftAsync(SessionId, Chips)).Value;

        check.Check!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        _checker.Checked.ShouldBe([_store.DraftFolder(User, SessionId, Chips)]);
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_checker_the_check_is_null()
    {
        _store.SeedDraft(User, SessionId, Chips);

        (await _service.CheckDraftAsync(SessionId, Chips)).Value.Check.ShouldBeNull();
    }

    [Fact]
    public async Task Checking_a_draft_that_isnt_there_is_not_found_and_doesnt_run_the_checker()
    {
        (await _service.CheckDraftAsync(SessionId, Chips)).Error.Code.ShouldEndWith(".NotFound");
        _checker.Checked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_a_draft_off_and_on_raises_draft_off_then_draft_on_with_the_session()
    {
        _store.SeedDraft(User, SessionId, Chips);

        var off = (await _service.SetDraftOnAsync(SessionId, Chips, on: false)).Value;
        off.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        var on = (await _service.SetDraftOnAsync(SessionId, Chips, on: true)).Value;
        on.Off.ShouldBeNull();

        _events.Broadcasts.Select(b => ((ModsChanged)b.DomainEvent!).Payload).Select(p => (p.Reason, p.Name, p.SessionId))
            .ShouldBe([("draft-off", Chips, SessionId), ("draft-on", Chips, SessionId)]);
    }

    [Fact]
    public async Task Turning_off_a_draft_that_isnt_there_is_not_found_and_raises_nothing()
    {
        (await _service.SetDraftOnAsync(SessionId, Chips, on: false)).Error.Code.ShouldEndWith(".NotFound");
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Draft_strikes_turn_the_draft_off_with_the_error()
    {
        _store.SeedDraft(User, SessionId, Chips);

        var draft = (await _service.RecordDraftStrikesAsync(SessionId, Chips, "boom")).Value;

        var off = draft.Off.ShouldNotBeNull();
        (off.By, off.Error).ShouldBe((ModOffBy.Strikes, "boom"));
        ChangedOnce().ShouldBe(("strikes", Chips, SessionId));
    }

    // ── Keep ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Keep_stores_a_version_with_the_sessions_title_the_note_and_the_checkers_report()
    {
        _store.SeedDraft(User, SessionId, Chips);
        _checker.Report = Json("""{"ok":true,"sha256":"abc"}""");

        var mod = (await _service.KeepAsync(SessionId, Chips, "show failing names")).Value;

        (mod.Name, mod.Active).ShouldBe((Chips, 1));
        var version = mod.Versions.ShouldHaveSingleItem();
        (version.SessionTitle, version.Note, version.SessionId).ShouldBe(("Tidy the build output", "show failing names", SessionId));
        version.Check!.Value.GetProperty("sha256").GetString().ShouldBe("abc");
        ChangedOnce().ShouldBe(("kept", Chips, SessionId));
        (await _service.ListDraftsAsync(SessionId)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_stores_the_report_the_caller_shows_and_doesnt_check_again()
    {
        _store.SeedDraft(User, SessionId, Chips);
        _checker.Report = Json("""{"ok":true,"from":"checker"}""");

        var mod = (await _service.KeepAsync(SessionId, Chips, null, Json("""{"ok":true,"from":"caller"}"""))).Value;

        mod.Versions.Single().Check!.Value.GetProperty("from").GetString().ShouldBe("caller");
        _checker.Checked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_without_a_checker_keeps_without_a_report()
    {
        _store.SeedDraft(User, SessionId, Chips);

        var mod = (await _service.KeepAsync(SessionId, Chips, null)).Value;

        mod.Versions.Single().Check.ShouldBeNull();
    }

    [Fact]
    public async Task Keep_refuses_a_report_that_isnt_ok_and_names_the_first_error()
    {
        _store.SeedDraft(User, SessionId, Chips);
        _checker.Report = Json("""{"ok":false,"errors":[{"message":"It reads the global fetch.","line":3,"column":1},{"message":"second"}]}""");

        var result = await _service.KeepAsync(SessionId, Chips, null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldContain("It reads the global fetch.");
        _store.Writes.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_refuses_a_callers_report_that_isnt_ok_too()
    {
        _store.SeedDraft(User, SessionId, Chips);

        var result = await _service.KeepAsync(SessionId, Chips, null, Json("""{"ok":false,"errors":[{"message":"bad"}]}"""));

        result.Error.Description.ShouldContain("bad");
        _store.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_refuses_a_note_over_2000_characters_and_takes_one_of_exactly_2000()
    {
        _store.SeedDraft(User, SessionId, Chips);

        var tooLong = await _service.KeepAsync(SessionId, Chips, new string('x', 2001));
        tooLong.Error.Code.ShouldStartWith("Validation.");
        _events.Broadcasts.ShouldBeEmpty();

        (await _service.KeepAsync(SessionId, Chips, new string('x', 2000))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Keep_turns_a_store_refusal_into_a_validation_error_and_raises_nothing()
    {
        _store.SeedDraft(User, SessionId, Chips, withManifest: false);

        var result = await _service.KeepAsync(SessionId, Chips, null);

        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldBe($"There is no draft of {Chips} to keep.");
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_of_a_draft_that_isnt_there_is_not_found()
    {
        (await _service.KeepAsync(SessionId, Chips, null)).Error.Code.ShouldEndWith(".NotFound");
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_works_without_a_session_record()
    {
        _store.SeedDraft(User, "gone-session", Chips);

        var mod = (await _service.KeepAsync("gone-session", Chips, null)).Value;

        mod.Versions.Single().SessionTitle.ShouldBeNull();
    }

    [Fact]
    public async Task A_second_keep_makes_version_2_active_and_turns_the_mod_on()
    {
        SeedChips(count: 1, off: new ModOff(ModOffBy.User, DateTimeOffset.UnixEpoch));
        _store.SeedDraft(User, SessionId, Chips);

        var mod = (await _service.KeepAsync(SessionId, Chips, null)).Value;

        (mod.Active, mod.Off).ShouldBe((2, null));
    }

    // ── Names ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("fleet-x")]
    public async Task An_invalid_name_is_not_found_everywhere_and_never_reaches_the_store(string name)
    {
        var results = new[]
        {
            (await _service.UseVersionAsync(name, 1)).Error,
            (await _service.UndoAsync(name)).Error,
            (await _service.SetOnAsync(name, true)).Error,
            (await _service.RecordStrikesAsync(name, "x")).Error,
            (await _service.ReadDraftFilesAsync(SessionId, name)).Error,
            (await _service.CheckDraftAsync(SessionId, name)).Error,
            (await _service.SetDraftOnAsync(SessionId, name, true)).Error,
            (await _service.RecordDraftStrikesAsync(SessionId, name, "x")).Error,
            (await _service.KeepAsync(SessionId, name, null)).Error,
        };

        results.ShouldAllBe(error => error.Code.EndsWith(".NotFound", StringComparison.Ordinal));
        _store.Writes.ShouldBeEmpty();
        _checker.Checked.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public async Task An_invalid_session_id_is_not_found(string sessionId)
    {
        (await _service.ListDraftsAsync(sessionId)).Error.Code.ShouldEndWith(".NotFound");
        (await _service.KeepAsync(sessionId, Chips, null)).Error.Code.ShouldEndWith(".NotFound");
        (await _service.SetDraftOnAsync(sessionId, Chips, false)).Error.Code.ShouldEndWith(".NotFound");
        _events.Broadcasts.ShouldBeEmpty();
    }

    // ── Safe mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task Safe_mode_is_set_and_cleared_and_says_so()
    {
        var on = await _service.SetSafeModeAsync(true);
        on.SafeMode.ShouldBeTrue();
        _safeMode.IsOn.ShouldBeTrue();
        ChangedOnce().Reason.ShouldBe("safe-mode");

        var off = await _service.SetSafeModeAsync(false);
        off.SafeMode.ShouldBeFalse();
        _safeMode.IsOn.ShouldBeFalse();
        _events.Broadcasts.Count.ShouldBe(2);
    }

    [Fact]
    public void A_mods_changed_event_has_no_name_or_session_unless_it_is_about_one()
    {
        var json = JsonSerializer.Serialize<DomainEvent>(
            new ModsChanged { Payload = new ModsChangedPayload { Reason = "safe-mode" } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        json.ShouldBe("""{"type":"mods.changed","payload":{"reason":"safe-mode"}}""");
    }
}
