using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>The agent's mod tools: write, check, reload and keep, each checking the switch, safe mode, the name and the draft.</summary>
public sealed class ModBridgeTests
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "harness-1";
    private const string SessionId = "session-1";
    private const string Owner = "owner-user";
    private const string Chips = "test-chips";
    private const string DraftId = $"{Chips}@draft:{SessionId}";

    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeModChecker _checker = new(Json("""{"ok":true,"name":"test-chips","version":"0.1.0"}"""));
    private readonly FakeModHost _host = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ModsSafeMode _safeMode = new();
    private readonly FakeCallers _callers = new();
    private readonly ModKeepRequests _keepRequests = new(TimeProvider.System);
    private readonly ModBridge _bridge;

    public ModBridgeTests()
    {
        _sessions.Seed(new Session { Id = SessionId, Title = "Tidy the build output" });
        _preferences.Seed(ModsFeature.PreferenceKey, "true");
        _callers.Caller = new HarnessCanvasCaller(SessionId, Owner);

        var user = new ScopedUser();
        var service = new ModService(_store, _checker, _events, user, _safeMode, _sessions, new Host.FakeFiles(), TimeProvider.System, _host, _keepRequests);
        _bridge = new ModBridge([_callers], user, new ModsFeature(new FleetOptions(), _preferences, _safeMode, user), _safeMode, service, _host);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Manifest = """{"name":"test-chips","version":"0.1.0","description":"Draws test runs as counts","hooks":"./mod.ts"}""";

    private static List<ModFile> Files(params string[] paths) => paths.Select(p => new ModFile(p, p == "mod.json" ? Manifest : $"// {p}")).ToList();

    private void SeedDraft(ModOff? off = null) => _store.SeedDraft(Owner, SessionId, Chips, off);

    private Task<CanvasResult<CanvasToolOutput>> Call(string tool, string? name = Chips)
        => tool switch
        {
            "write" => _bridge.WriteAsync(Token, HarnessSessionId, name, Files("mod.json", "mod.ts")),
            "check" => _bridge.CheckAsync(Token, HarnessSessionId, name),
            "reload" => _bridge.ReloadAsync(Token, HarnessSessionId, name),
            "keep" => _bridge.KeepAsync(Token, HarnessSessionId, name, null),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

    private static string Failure(CanvasResult<CanvasToolOutput> result, CanvasErrorKind kind)
    {
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(kind);
        return result.Error!.Message;
    }

    // ── The checks every call makes ─────────────────────────────────────

    [Theory]
    [InlineData("write")]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("keep")]
    public async Task An_unknown_caller_is_not_found(string tool)
    {
        _callers.Caller = null;

        Failure(await Call(tool), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Fact]
    public async Task A_call_without_a_token_or_session_is_an_unknown_caller()
    {
        Failure(await _bridge.CheckAsync(null, HarnessSessionId, Chips), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
        Failure(await _bridge.CheckAsync(Token, " ", Chips), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("keep")]
    public async Task Every_tool_is_refused_while_mods_are_off(string tool)
    {
        SeedDraft();
        _preferences.Seed(ModsFeature.PreferenceKey, "false");

        Failure(await Call(tool), CanvasErrorKind.Refused).ShouldBe(ModBridge.TurnedOffMessage);
        _host.Reloaded.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("keep")]
    public async Task Reload_and_keep_are_refused_in_safe_mode(string tool)
    {
        SeedDraft();
        _safeMode.Set(Owner, true);

        Failure(await Call(tool), CanvasErrorKind.Refused).ShouldBe(ModBridge.SafeModeRefusal);
        _host.Reloaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task In_safe_mode_a_write_is_kept_but_not_loaded()
    {
        _safeMode.Set(Owner, true);

        var result = await Call("write");

        result.Value!.Output.ShouldEndWith(ModBridge.SafeModeNote);
        _store.Writes.ShouldHaveSingleItem();
        _host.Reloaded.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("write")]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("keep")]
    public async Task A_name_that_isnt_a_mod_name_is_invalid(string tool)
        => Failure(await Call(tool, "Test Chips"), CanvasErrorKind.Invalid).ShouldStartWith("Test Chips isn't a mod name");

    [Theory]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("keep")]
    public async Task A_draft_the_session_doesnt_have_is_not_found(string tool)
        => Failure(await Call(tool), CanvasErrorKind.NotFound)
            .ShouldBe("There's no draft test-chips in this session. Write it with fleet_mod_write first.");

    // ── fleet_mod_write ─────────────────────────────────────────────────

    [Fact]
    public async Task Write_writes_the_files_reloads_the_draft_and_says_it_loaded()
    {
        var result = await Call("write");

        result.Value!.Title.ShouldBe($"Wrote {Chips}");
        result.Value!.Output.ShouldBe("Wrote mod.json, mod.ts to the draft test-chips.\ntest-chips loaded in this session.");
        _host.Reloaded.ShouldBe([(Owner, SessionId, Chips)]);
        // The card refetches after the load too, when its problem may have changed.
        _events.Broadcasts.Select(b => b.DomainEvent.ShouldBeOfType<ModsChanged>().Payload.Reason).ShouldBe(["draft-written", "draft-loaded"]);
    }

    [Fact]
    public async Task Write_says_why_the_draft_didnt_load_with_the_checks_errors()
    {
        _host.OnReload = (_, _) => _host.Problems[DraftId] = new ModLoadProblem(
            "The check found a problem.",
            Json("""{"ok":false,"errors":[{"line":3,"column":7,"code":"global","message":"fetch isn't available to mods"},{"code":"manifest","message":"no hooks"}]}"""));

        var result = await Call("write");

        result.Value!.Output.ShouldEndWith(
            "test-chips didn't load: The check found a problem.\nerror 3:7 global: fetch isn't available to mods\nerror - manifest: no hooks");
    }

    [Fact]
    public async Task Write_says_when_the_mod_host_isnt_running()
    {
        _host.Status = new ModHostStatus(ModHostStates.NotReady, "The mod runtime (Bun) isn't installed yet.", null, null, null, 0);

        var result = await Call("write");

        result.Value!.Output.ShouldEndWith("test-chips isn't loaded: the mod host is not-ready (The mod runtime (Bun) isn't installed yet.).");
    }

    [Fact]
    public async Task Write_to_a_draft_three_strikes_turned_off_says_its_off_and_how_to_turn_it_on()
    {
        SeedDraft(off: new ModOff(ModOffBy.Strikes, DateTimeOffset.UnixEpoch, "TypeError: counts is undefined"));

        var result = await Call("write");

        result.Value!.Output.ShouldEndWith(
            "test-chips isn't loaded: it's off in this session after three failures in a row (TypeError: counts is undefined). "
            + "Once it's fixed, fleet_mod_reload turns it back on.");
    }

    [Fact]
    public async Task Write_to_a_draft_the_user_turned_off_says_so_and_to_ask_before_turning_it_on()
    {
        SeedDraft(off: new ModOff(ModOffBy.User, DateTimeOffset.UnixEpoch));

        var result = await Call("write");

        result.Value!.Output.ShouldEndWith(
            "test-chips isn't loaded: the user turned it off in this session. Ask them before turning it back on with fleet_mod_reload.");
    }

    [Fact]
    public async Task Write_with_an_incomplete_mod_json_says_so_instead_of_loaded()
    {
        var result = await _bridge.WriteAsync(Token, HarnessSessionId, Chips, [new ModFile("mod.json", """{"name":"test-chips"}"""), new ModFile("mod.ts", "//")]);

        result.Value!.Output.ShouldEndWith(
            "test-chips isn't loaded: its mod.json is missing or incomplete. It needs name, version, description and hooks.");
    }

    [Fact]
    public async Task Reload_of_a_draft_without_a_whole_mod_json_says_so_instead_of_loaded()
    {
        _store.SeedDraft(Owner, SessionId, Chips, withManifest: false);

        var result = await Call("reload");

        result.Value!.Output.ShouldBe("test-chips isn't loaded: its mod.json is missing or incomplete. It needs name, version, description and hooks.");
    }

    [Fact]
    public async Task Write_refuses_no_files_a_file_without_a_path_and_what_the_store_refuses()
    {
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, []), CanvasErrorKind.Invalid).ShouldStartWith("Send the files");
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, null), CanvasErrorKind.Invalid).ShouldStartWith("Send the files");
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, [new ModFile("", "x")]), CanvasErrorKind.Invalid).ShouldStartWith("Send the files");
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, Files("../x.ts")), CanvasErrorKind.Invalid)
            .ShouldBe("A file's path stays inside the mod's folder.");
        _host.Reloaded.ShouldBeEmpty();
    }

    // ── fleet_mod_check ─────────────────────────────────────────────────

    [Fact]
    public async Task Check_returns_the_report()
    {
        SeedDraft();

        var result = await Call("check");

        result.Value!.Title.ShouldBe($"Checked {Chips}");
        Json(result.Value!.Output).GetProperty("name").GetString().ShouldBe(Chips);
    }

    [Fact]
    public async Task Check_without_a_checker_is_refused()
    {
        SeedDraft();
        _checker.Report = null;

        Failure(await Call("check"), CanvasErrorKind.Refused).ShouldBe("No checker is available, so there is no check report.");
    }

    // ── fleet_mod_reload ────────────────────────────────────────────────

    [Fact]
    public async Task Reload_reloads_and_says_it_loaded()
    {
        SeedDraft();

        var result = await Call("reload");

        result.Value!.Title.ShouldBe($"Reloaded {Chips}");
        result.Value!.Output.ShouldBe("test-chips loaded in this session.");
        _host.Reloaded.ShouldBe([(Owner, SessionId, Chips)]);
        var loaded = _events.Broadcasts.Select(b => b.DomainEvent.ShouldBeOfType<ModsChanged>().Payload).ShouldHaveSingleItem();
        (loaded.Reason, loaded.Name, loaded.SessionId).ShouldBe(("draft-loaded", Chips, SessionId));
    }

    [Fact]
    public async Task Reload_turns_a_draft_three_strikes_turned_off_back_on_first()
    {
        SeedDraft(off: new ModOff(ModOffBy.Strikes, DateTimeOffset.UnixEpoch, "boom"));
        _host.OnReload = (session, name) => _store.GetDraftAsync(Owner, session, name).Result!.Off.ShouldBeNull();

        var result = await Call("reload");

        result.Value!.Output.ShouldBe("test-chips loaded in this session.\nIt was off; it's on again.");
        _host.Reloaded.ShouldHaveSingleItem();
    }

    // ── fleet_mod_keep ──────────────────────────────────────────────────

    [Fact]
    public async Task Keep_raises_the_request_with_the_note_and_returns_at_once()
    {
        SeedDraft();

        var result = await _bridge.KeepAsync(Token, HarnessSessionId, Chips, "Shows pass and fail counts");

        result.Value!.Title.ShouldBe($"Asked to keep {Chips}");
        result.Value!.Output.ShouldStartWith("Asked the user to keep test-chips.");
        _keepRequests.Get(Owner, SessionId, Chips)!.Note.ShouldBe("Shows pass and fail counts");
        _events.Broadcasts.Select(b => b.DomainEvent.ShouldBeOfType<ModsChanged>().Payload.Reason).ShouldBe(["keep-requested"]);
    }

    // ── Reading the arguments ───────────────────────────────────────────

    [Fact]
    public void Files_come_from_a_list_or_from_JSON_text()
    {
        ModBridge.ReadFiles(Json("""[{"path":"mod.ts","content":"x"}]""")).ShouldBe([new ModFile("mod.ts", "x")]);
        ModBridge.ReadFiles(Json("""{"f":"[{\"path\":\"mod.ts\",\"content\":\"x\"}]"}""").GetProperty("f")).ShouldBe([new ModFile("mod.ts", "x")]);
    }

    [Fact]
    public void An_entry_without_a_path_or_content_has_an_empty_path_and_anything_but_a_list_is_none()
    {
        ModBridge.ReadFiles(Json("""[{"path":"mod.ts"},"mod.ts"]""")).ShouldBe([new ModFile("", ""), new ModFile("", "")]);
        ModBridge.ReadFiles(Json("""{"f":"not json"}""").GetProperty("f")).ShouldBeNull();
        ModBridge.ReadFiles(Json("""{"path":"mod.ts"}""")).ShouldBeNull();
        ModBridge.ReadFiles(default).ShouldBeNull();
    }

    private sealed class ScopedUser : IUserContext, IBackgroundUserScope
    {
        public string UserId { get; private set; } = "anonymous";
        public string? Email => null;
        public string? DisplayName => UserId;
        public bool IsAuthenticated => true;

        public IDisposable Begin(string userId)
        {
            UserId = userId;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public HarnessCanvasCaller? Caller { get; set; }

        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(Caller);
    }
}
