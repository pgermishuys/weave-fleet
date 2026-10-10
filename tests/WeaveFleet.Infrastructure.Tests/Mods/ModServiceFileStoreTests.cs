using System.Text.Json;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Mods;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Mods;

/// <summary>
/// <see cref="ModService"/> on the real <see cref="FileModVersionStore"/>: what the service answers, what is on disk and
/// which <c>mods.changed</c> events were raised, with no fake store in between.
/// </summary>
public sealed class ModServiceFileStoreTests : IDisposable
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";
    private const string Chips = "test-chips";
    private const string Alpha = "ses_alpha";
    private const string Beta = "ses_beta";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-modsvc-{Guid.NewGuid():N}");
    private readonly List<string> _extraRoots = [];
    private readonly List<FileModVersionStore> _extraStores = [];
    private readonly FileModVersionStore _store;
    private readonly Rig _rig;

    public ModServiceFileStoreTests()
    {
        _store = new FileModVersionStore(_root);
        _rig = new Rig(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var store in _extraStores)
            store.Dispose();
        foreach (var folder in _extraRoots.Append(_root))
            if (Directory.Exists(folder))
                Remove(folder);
    }

    private static void Remove(string folder)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, (UnixFileMode)0b111_111_111);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                entry.Delete();
            else if (entry is DirectoryInfo directory)
                Remove(directory.FullName);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        Directory.Delete(folder);
    }

    private sealed class SettableUser(string userId) : IUserContext
    {
        public string UserId { get; set; } = userId;
        public string? Email => null;
        public string? DisplayName => UserId;
        public bool IsAuthenticated => true;
    }

    private sealed class RecordingChecker : IModChecker
    {
        public JsonElement? Report { get; set; }
        public List<string> Checked { get; } = [];

        public Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default)
        {
            lock (Checked)
                Checked.Add(folder);
            return Task.FromResult(Report);
        }
    }

    /// <summary>One service on a store, with its own recorder, checker and user.</summary>
    private sealed class Rig
    {
        public Rig(FileModVersionStore store)
        {
            Store = store;
            Sessions.Seed(new Session { Id = Alpha, Title = "Tidy the build output" }, new Session { Id = Beta, Title = "Rename the chips" });
            Service = new ModService(store, Checker, Events, User, SafeMode, Sessions, TimeProvider.System);
        }

        public FileModVersionStore Store { get; }
        public RecordingChecker Checker { get; } = new();
        public FakeEventBroadcaster Events { get; } = new();
        public SettableUser User { get; } = new(Alice);
        public ModsSafeMode SafeMode { get; } = new();
        public InMemorySessionRepository Sessions { get; } = new();
        public ModService Service { get; }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Manifest(string version) => $$"""{"name":"test-chips","version":"{{version}}","description":"Shows chips.","hooks":"mod.ts"}""";

    private static string Draft(Rig rig, string session, string version = "0.1.0", string name = Chips)
    {
        var folder = rig.Store.DraftFolder(rig.User.UserId, session, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), Manifest(version));
        File.WriteAllText(Path.Combine(folder, "mod.ts"), $"export default {{ from: '{session}' }};\n");
        return folder;
    }

    private string ModFolder(string user = Alice, string name = Chips) => Path.GetDirectoryName(_store.VersionFolder(user, name, 1))!;

    private string SessionDrafts(string session, string user = Alice) => Path.GetDirectoryName(_store.DraftFolder(user, session, Chips))!;

    private static (string Reason, string? Name, string? SessionId, string? UserId) Event(FakeEventBroadcaster.BroadcastRecord sent)
    {
        (sent.Topic, sent.Type).ShouldBe(("sessions", "mods.changed"));
        var payload = sent.DomainEvent.ShouldBeOfType<ModsChanged>().Payload;
        sent.Payload.GetProperty("reason").GetString().ShouldBe(payload.Reason);
        return (payload.Reason, payload.Name, payload.SessionId, sent.UserId);
    }

    private static string[] Reasons(Rig rig) => rig.Events.Broadcasts.Select(b => Event(b).Reason).ToArray();

    /// <summary>Keeps versions 1..count from a fresh draft each, then forgets the events.</summary>
    private static async Task KeepVersions(Rig rig, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Draft(rig, $"ses_seed{i}", $"0.{i}.0");
            (await rig.Service.KeepAsync($"ses_seed{i}", Chips, null)).IsSuccess.ShouldBeTrue();
        }

        rig.Events.Broadcasts.Clear();
    }

    private static void NoTemporaryFiles(string folder)
    {
        if (Directory.Exists(folder))
            Directory.GetFileSystemEntries(folder, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    // ── Keep ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Keep_makes_version_1_active_and_on_removes_the_draft_and_says_so_once()
    {
        var draft = Draft(_rig, Alpha);

        var result = await _rig.Service.KeepAsync(Alpha, Chips, "first go");

        var mod = result.Value;
        (mod.Name, mod.Active, mod.ActiveVersion, mod.Description, mod.Off).ShouldBe((Chips, 1, "0.1.0", "Shows chips.", null));
        var version = mod.Versions.ShouldHaveSingleItem();
        (version.Number, version.SessionId, version.SessionTitle, version.Note).ShouldBe((1, Alpha, "Tidy the build output", "first go"));

        File.Exists(Path.Combine(ModFolder(), "v1", "mod.ts")).ShouldBeTrue();
        File.Exists(Path.Combine(ModFolder(), "versions.json")).ShouldBeTrue();
        Directory.Exists(draft).ShouldBeFalse();
        Directory.Exists(SessionDrafts(Alpha)).ShouldBeFalse();
        NoTemporaryFiles(ModFolder());

        var sent = _rig.Events.Broadcasts.ShouldHaveSingleItem();
        Event(sent).ShouldBe(("kept", Chips, Alpha, Alice));
    }

    [Fact]
    public async Task Keeping_again_from_another_session_makes_version_2()
    {
        await KeepVersions(_rig, 1);
        Draft(_rig, Beta, "0.2.0");

        var mod = (await _rig.Service.KeepAsync(Beta, Chips, null)).Value;

        (mod.Active, mod.ActiveVersion, mod.Off).ShouldBe((2, "0.2.0", null));
        mod.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        mod.Versions[1].SessionTitle.ShouldBe("Rename the chips");
        Directory.Exists(Path.Combine(ModFolder(), "v1")).ShouldBeTrue();
        Directory.Exists(Path.Combine(ModFolder(), "v2")).ShouldBeTrue();
        Event(_rig.Events.Broadcasts.ShouldHaveSingleItem()).ShouldBe(("kept", Chips, Beta, Alice));
    }

    [Fact]
    public async Task The_checker_is_asked_about_the_staged_copy_and_its_report_is_stored()
    {
        _rig.Checker.Report = Json("""{"ok":true,"errors":[]}""");
        var draft = Draft(_rig, Alpha);

        var mod = (await _rig.Service.KeepAsync(Alpha, Chips, null)).Value;

        var asked = _rig.Checker.Checked.ShouldHaveSingleItem();
        asked.ShouldNotBe(draft);
        asked.ShouldStartWith(ModFolder());
        asked.ShouldNotContain(Path.DirectorySeparatorChar + "drafts" + Path.DirectorySeparatorChar);
        mod.Versions[0].Check!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModFolder(), "versions.json")));
        index.RootElement.GetProperty("versions")[0].GetProperty("check").GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_report_the_user_saw_is_stored_and_the_checker_isnt_asked()
    {
        Draft(_rig, Alpha);

        var mod = (await _rig.Service.KeepAsync(Alpha, Chips, null, Json("""{"ok":true,"seen":1}"""))).Value;

        _rig.Checker.Checked.ShouldBeEmpty();
        mod.Versions[0].Check!.Value.GetProperty("seen").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_report_that_is_not_ok_refuses_the_keep_and_leaves_the_draft_alone()
    {
        _rig.Checker.Report = Json("""{"ok":false,"errors":[{"message":"hooks must default-export an object"}]}""");
        var draft = Draft(_rig, Alpha);

        var result = await _rig.Service.KeepAsync(Alpha, Chips, null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("Validation");
        result.Error.Description.ShouldContain("hooks must default-export an object");
        (await _rig.Service.ListAsync()).Mods.ShouldBeEmpty();
        _rig.Events.Broadcasts.ShouldBeEmpty();
        File.Exists(Path.Combine(draft, "mod.ts")).ShouldBeTrue();
        Directory.Exists(Path.Combine(ModFolder(), "v1")).ShouldBeFalse();
        NoTemporaryFiles(ModFolder());
        if (Directory.Exists(ModFolder()))
            Directory.GetDirectories(ModFolder()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_of_a_session_with_no_such_draft_is_not_found_and_raises_nothing()
    {
        var result = await _rig.Service.KeepAsync(Alpha, Chips, null);

        result.Error.Code.ShouldEndWith(".NotFound");
        _rig.Events.Broadcasts.ShouldBeEmpty();
    }

    // ── Undo and use a version ──────────────────────────────────────────

    [Fact]
    public async Task Undo_of_version_2_makes_version_1_active_and_on_even_when_the_mod_was_off_by_strikes()
    {
        await KeepVersions(_rig, 2);
        (await _rig.Service.RecordStrikesAsync(Chips, "TypeError: nope")).IsSuccess.ShouldBeTrue();
        _rig.Events.Broadcasts.Clear();

        var mod = (await _rig.Service.UndoAsync(Chips)).Value;

        (mod.Active, mod.Off).ShouldBe((1, null));
        var history = await _store.GetAsync(Alice, Chips);
        (history.Active, history.Off, history.Versions.Count).ShouldBe((1, null, 2));
        Directory.Exists(Path.Combine(ModFolder(), "v2")).ShouldBeTrue();
        Event(_rig.Events.Broadcasts.ShouldHaveSingleItem()).ShouldBe(("undone", Chips, null, Alice));
    }

    [Fact]
    public async Task Undo_of_version_1_turns_the_mod_off_by_the_user_and_a_second_undo_has_nothing_to_do()
    {
        await KeepVersions(_rig, 1);

        var mod = (await _rig.Service.UndoAsync(Chips)).Value;

        mod.Active.ShouldBe(1);
        mod.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        Event(_rig.Events.Broadcasts.ShouldHaveSingleItem()).Reason.ShouldBe("undone");
        _rig.Events.Broadcasts.Clear();

        var again = await _rig.Service.UndoAsync(Chips);

        again.IsFailure.ShouldBeTrue();
        again.Error.Description.ShouldContain("Nothing to undo");
        _rig.Events.Broadcasts.ShouldBeEmpty();
        (await _store.GetAsync(Alice, Chips)).Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
    }

    [Fact]
    public async Task Using_a_version_makes_it_active_and_on_and_clears_strikes()
    {
        await KeepVersions(_rig, 2);
        await _rig.Service.UndoAsync(Chips);
        await _rig.Service.RecordStrikesAsync(Chips, "boom");
        _rig.Events.Broadcasts.Clear();

        var mod = (await _rig.Service.UseVersionAsync(Chips, 2)).Value;

        (mod.Active, mod.ActiveVersion, mod.Off).ShouldBe((2, "0.2.0", null));
        Event(_rig.Events.Broadcasts.ShouldHaveSingleItem()).ShouldBe(("version", Chips, null, Alice));
    }

    [Fact]
    public async Task Using_a_version_that_isnt_there_is_not_found_and_raises_nothing()
    {
        await KeepVersions(_rig, 1);

        (await _rig.Service.UseVersionAsync(Chips, 7)).Error.Code.ShouldEndWith(".NotFound");
        _rig.Events.Broadcasts.ShouldBeEmpty();
        (await _store.GetAsync(Alice, Chips)).Active.ShouldBe(1);
    }

    // ── Off, on, strikes, drafts ────────────────────────────────────────

    [Fact]
    public async Task A_mod_is_turned_off_and_on_and_strikes_keep_the_last_error()
    {
        await KeepVersions(_rig, 1);

        (await _rig.Service.SetOnAsync(Chips, false)).Value.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        (await _rig.Service.SetOnAsync(Chips, true)).Value.Off.ShouldBeNull();
        var struck = (await _rig.Service.RecordStrikesAsync(Chips, "TypeError: nope")).Value;

        var off = struck.Off.ShouldNotBeNull();
        (off.By, off.Error).ShouldBe((ModOffBy.Strikes, "TypeError: nope"));
        (await _store.GetAsync(Alice, Chips)).Off!.Error.ShouldBe("TypeError: nope");
        Reasons(_rig).ShouldBe(["off", "on", "strikes"]);
    }

    [Fact]
    public async Task Draft_off_on_and_strikes_show_in_the_drafts_list()
    {
        Draft(_rig, Alpha);

        (await _rig.Service.ListDraftsAsync(Alpha)).Value.ShouldHaveSingleItem().Off.ShouldBeNull();

        (await _rig.Service.SetDraftOnAsync(Alpha, Chips, false)).Value.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        (await _rig.Service.ListDraftsAsync(Alpha)).Value.Single().Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);

        (await _rig.Service.SetDraftOnAsync(Alpha, Chips, true)).Value.Off.ShouldBeNull();
        (await _rig.Service.ListDraftsAsync(Alpha)).Value.Single().Off.ShouldBeNull();

        await _rig.Service.RecordDraftStrikesAsync(Alpha, Chips, "ReferenceError: x");
        var struck = (await _rig.Service.ListDraftsAsync(Alpha)).Value.Single();
        var off = struck.Off.ShouldNotBeNull();
        (off.By, off.Error).ShouldBe((ModOffBy.Strikes, "ReferenceError: x"));
        (struck.Description, struck.Version, struck.Kept).ShouldBe(("Shows chips.", "0.1.0", null));

        Reasons(_rig).ShouldBe(["draft-off", "draft-on", "strikes"]);
        _rig.Events.Broadcasts.ShouldAllBe(b => Event(b).SessionId == Alpha);
    }

    [Fact]
    public async Task A_draft_that_isnt_there_is_not_found_and_raises_nothing()
    {
        (await _rig.Service.SetDraftOnAsync(Alpha, Chips, false)).Error.Code.ShouldEndWith(".NotFound");
        _rig.Events.Broadcasts.ShouldBeEmpty();
    }

    // ── Two users ───────────────────────────────────────────────────────

    [Fact]
    public async Task One_users_keep_isnt_in_the_other_users_list()
    {
        Draft(_rig, Alpha);
        (await _rig.Service.KeepAsync(Alpha, Chips, null)).IsSuccess.ShouldBeTrue();

        _rig.User.UserId = Bob;

        (await _rig.Service.ListAsync()).Mods.ShouldBeEmpty();
        (await _rig.Service.GetAsync(Chips)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.UndoAsync(Chips)).Error.Code.ShouldEndWith(".NotFound");

        _rig.User.UserId = Alice;
        (await _rig.Service.ListAsync()).Mods.ShouldHaveSingleItem().Name.ShouldBe(Chips);
    }

    [Fact]
    public async Task Safe_mode_is_per_user_and_its_event_carries_the_users_id()
    {
        (await _rig.Service.SetSafeModeAsync(true)).SafeMode.ShouldBeTrue();

        (await _rig.Service.ListAsync()).SafeMode.ShouldBeTrue();
        _rig.User.UserId = Bob;
        (await _rig.Service.ListAsync()).SafeMode.ShouldBeFalse();

        var sent = _rig.Events.Broadcasts.ShouldHaveSingleItem();
        Event(sent).ShouldBe(("safe-mode", null, null, Alice));
    }

    // ── Concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Undo_and_a_keep_of_version_3_at_the_same_time_leave_the_index_consistent()
    {
        for (var i = 0; i < 20; i++)
        {
            var root = Path.Combine(Path.GetTempPath(), $"fleet-modsvc-race-{Guid.NewGuid():N}");
            _extraRoots.Add(root);
            var store = new FileModVersionStore(root);
            _extraStores.Add(store);
            var rig = new Rig(store);
            await KeepVersions(rig, 2);
            Draft(rig, Alpha, "0.3.0");

            using var gate = new ManualResetEventSlim();
            var undo = Task.Run(() => { gate.Wait(); return rig.Service.UndoAsync(Chips); });
            var keep = Task.Run(() => { gate.Wait(); return rig.Service.KeepAsync(Alpha, Chips, null); });
            gate.Set();
            var results = await Task.WhenAll(undo, keep);

            var history = await store.GetAsync(Alice, Chips);
            // Undo first then keep: 3 active. Keep first then undo: 2 active.
            history.Versions.Select(v => v.Number).ShouldBe([1, 2, 3]);
            history.Active.ShouldNotBeNull();
            history.Active!.Value.ShouldBeOneOf(2, 3);
            foreach (var v in history.Versions)
                Directory.Exists(store.VersionFolder(Alice, Chips, v.Number)).ShouldBeTrue();
            NoTemporaryFiles(root);

            rig.Events.Broadcasts.Count.ShouldBe(results.Count(r => r.IsSuccess));
            results[1].IsSuccess.ShouldBeTrue();
        }
    }

    // ── Show code ───────────────────────────────────────────────────────

    [Fact]
    public async Task Show_code_reads_a_kept_version_and_a_draft()
    {
        Draft(_rig, Alpha);
        await _rig.Service.KeepAsync(Alpha, Chips, null);
        Draft(_rig, Beta, "0.2.0");

        var version = (await _rig.Service.ReadVersionFilesAsync(Chips, 1)).Value.Files;
        version.Select(f => f.Path).Order().ShouldBe(["mod.json", "mod.ts"]);
        version.Single(f => f.Path == "mod.ts").Content.ShouldContain("ses_alpha");

        var draft = (await _rig.Service.ReadDraftFilesAsync(Beta, Chips)).Value.Files;
        draft.Single(f => f.Path == "mod.ts").Content.ShouldContain("ses_beta");

        (await _rig.Service.ReadVersionFilesAsync(Chips, 2)).Error.Code.ShouldEndWith(".NotFound");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("Chips")]
    [InlineData("drafts")]
    public async Task An_invalid_or_traversing_name_is_not_found_and_nothing_is_touched(string name)
    {
        Draft(_rig, Alpha);
        _rig.Events.Broadcasts.Clear();
        var before = Snapshot();

        (await _rig.Service.GetAsync(name)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.ReadVersionFilesAsync(name, 1)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.ReadDraftFilesAsync(Alpha, name)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.KeepAsync(Alpha, name, null)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.UndoAsync(name)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.SetDraftOnAsync(Alpha, name, false)).Error.Code.ShouldEndWith(".NotFound");
        (await _rig.Service.ReadDraftFilesAsync("../ses", Chips)).Error.Code.ShouldEndWith(".NotFound");

        _rig.Events.Broadcasts.ShouldBeEmpty();
        Snapshot().ShouldBe(before);
    }

    private string[] Snapshot()
        => Directory.Exists(_root)
            ? Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_root, p)).Order().ToArray()
            : [];
}
