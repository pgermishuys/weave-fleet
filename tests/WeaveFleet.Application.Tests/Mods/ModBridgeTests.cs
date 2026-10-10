using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModBridgeTests
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "harness-1";
    private const string SessionId = "session-1";
    private const string Owner = "owner-user";
    private const string Chips = "test-chips";

    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeModChecker _checker = new(Json("""{"ok":true,"name":"test-chips","version":"0.1.0"}"""));
    private readonly FakeModDraftRunner _runner = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ModsSafeMode _safeMode = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeCallers _callers = new();
    private readonly ModKeepRequests _keepRequests;
    private readonly ModService _service;
    private readonly ModBridge _bridge;

    public ModBridgeTests()
    {
        _sessions.Seed(new Session { Id = SessionId, Title = "Tidy the build output" });
        _preferences.Seed(ModsFeature.PreferenceKey, "true");
        _callers.Caller = new HarnessCanvasCaller(SessionId, Owner);

        var user = new ScopedUser();
        _keepRequests = new ModKeepRequests(_time);
        _service = new ModService(_store, _checker, _events, user, _safeMode, _sessions, _time, _runner, _keepRequests);
        var feature = new ModsFeature(new FleetOptions(), _preferences, _safeMode, user);
        _bridge = new ModBridge([_callers], user, feature, _safeMode, _service, _runner, _keepRequests);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<ModFile> Files(params string[] paths) => paths.Select(p => new ModFile(p, $"// {p}")).ToList();

    private void SeedDraft(string name = Chips, ModOff? off = null) => _store.SeedDraft(Owner, SessionId, name, off);

    private Task<CanvasResult<CanvasToolOutput>> Call(string tool, string? name = Chips, string token = Token)
        => tool switch
        {
            "write" => _bridge.WriteAsync(token, HarnessSessionId, name, Files("mod.json", "mod.ts")),
            "check" => _bridge.CheckAsync(token, HarnessSessionId, name),
            "reload" => _bridge.ReloadAsync(token, HarnessSessionId, name),
            "test" => _bridge.TestAsync(token, HarnessSessionId, name, "session.start", Json("{}")),
            "keep" => _bridge.KeepAsync(token, HarnessSessionId, name, null),
            "list" => _bridge.ListAsync(token, HarnessSessionId),
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
    [InlineData("test")]
    [InlineData("keep")]
    [InlineData("list")]
    public async Task An_unknown_caller_is_not_found(string tool)
    {
        _callers.Caller = null;

        Failure(await Call(tool), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Fact]
    public async Task A_call_without_a_token_or_session_is_an_unknown_caller()
    {
        Failure(await _bridge.ListAsync(null, HarnessSessionId), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
        Failure(await _bridge.ListAsync(Token, " "), CanvasErrorKind.NotFound).ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("test")]
    [InlineData("keep")]
    [InlineData("list")]
    public async Task Every_tool_is_refused_while_mods_are_off(string tool)
    {
        SeedDraft();
        _preferences.Seed(ModsFeature.PreferenceKey, "false");

        Failure(await Call(tool), CanvasErrorKind.Refused)
            .ShouldBe("Mods are turned off in Fleet's Settings, so the mod tools can't run. Ask the user to turn on Mods.");
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("test")]
    [InlineData("keep")]
    public async Task Reload_test_and_keep_are_refused_in_safe_mode(string tool)
    {
        SeedDraft();
        _safeMode.Set(Owner, true);

        Failure(await Call(tool), CanvasErrorKind.Refused)
            .ShouldBe("Fleet was started without mods, so drafts don't load. The user can turn mods back on from the banner.");
    }

    [Theory]
    [InlineData("write")]
    [InlineData("check")]
    [InlineData("list")]
    public async Task Write_check_and_list_work_in_safe_mode_and_say_the_draft_wont_load(string tool)
    {
        SeedDraft();
        _safeMode.Set(Owner, true);

        var result = await Call(tool);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Output.ShouldContain("Fleet was started without mods: the draft won't load until the user turns mods back on.");
    }

    [Theory]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("test")]
    [InlineData("keep")]
    public async Task Check_reload_test_and_keep_are_refused_when_the_runtime_isnt_ready(string tool)
    {
        SeedDraft();
        _runner.NotReady = "Bun is still starting.";

        Failure(await Call(tool), CanvasErrorKind.Refused).ShouldBe("The mod runtime isn't ready yet: Bun is still starting.");
    }

    [Fact]
    public async Task Write_still_writes_when_the_runtime_isnt_ready_and_says_the_check_didnt_run()
    {
        _runner.NotReady = "Bun is still starting.";

        var result = await Call("write");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Output.ShouldContain("The check didn't run: the mod runtime isn't ready yet (Bun is still starting.).");
        _store.Writes.ShouldContain($"write {SessionId}/{Chips} mod.json,mod.ts");
        _checker.Checked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("write", "Bad Name")]
    [InlineData("check", "Bad Name")]
    [InlineData("reload", "Chips")]
    [InlineData("test", "fleet-chips")]
    [InlineData("keep", "")]
    public async Task A_name_that_isnt_a_mod_name_is_invalid(string tool, string name)
    {
        Failure(await Call(tool, name), CanvasErrorKind.Invalid)
            .ShouldBe($"{name} isn't a mod name: use lowercase letters, digits and -, starting with a letter, 64 at most, not starting with fleet-.");
    }

    [Theory]
    [InlineData("check")]
    [InlineData("reload")]
    [InlineData("test")]
    [InlineData("keep")]
    public async Task A_missing_draft_is_not_found(string tool)
    {
        Failure(await Call(tool), CanvasErrorKind.NotFound).ShouldBe($"There's no draft {Chips} in this session. Write it with fleet_mod_write first.");
    }

    [Fact]
    public async Task The_checks_run_as_the_callers_owner()
    {
        SeedDraft();
        _store.SeedDraft("someone-else", SessionId, "other-mod");

        var result = await Call("list");

        result.Value!.Output.ShouldContain(Chips);
        result.Value!.Output.ShouldNotContain("other-mod");
    }

    // ── fleet_mod_write ─────────────────────────────────────────────────

    [Fact]
    public async Task Write_creates_the_draft_lists_the_paths_and_shows_the_check()
    {
        var report = Json("""{"ok":true,"name":"test-chips","version":"0.1.0"}""");
        _checker.Report = report;

        var result = await Call("write");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Title.ShouldBe($"Wrote {Chips}");
        result.Value!.Output.ShouldBe($"Wrote 2 file(s) to the draft {Chips}: mod.json, mod.ts.\n\n{ModCheckText.Format(report)}");
        (await _store.GetDraftAsync(Owner, SessionId, Chips)).ShouldNotBeNull();
        var changed = _events.Broadcasts.ShouldHaveSingleItem().DomainEvent.ShouldBeOfType<ModsChanged>().Payload;
        (changed.Reason, changed.Name, changed.SessionId).ShouldBe(("draft-written", Chips, SessionId));
    }

    [Fact]
    public async Task Write_adds_a_line_when_the_check_says_the_draft_wont_load()
    {
        var report = Json("""{"ok":false,"errors":[{"code":"parse","message":"Unexpected token","line":3}]}""");
        _checker.Report = report;

        var result = await Call("write");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Output.ShouldContain(ModCheckText.Format(report));
        result.Value!.Output.ShouldEndWith("The draft won't load until these errors are fixed.");
    }

    [Fact]
    public async Task Write_without_files_is_invalid()
    {
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, []), CanvasErrorKind.Invalid).ShouldNotBeNullOrWhiteSpace();
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, null), CanvasErrorKind.Invalid).ShouldNotBeNullOrWhiteSpace();
        Failure(await _bridge.WriteAsync(Token, HarnessSessionId, Chips, [new ModFile("", "x")]), CanvasErrorKind.Invalid).ShouldNotBeNullOrWhiteSpace();
        _store.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_with_a_path_the_store_refuses_is_invalid_with_its_message()
    {
        var result = await _bridge.WriteAsync(Token, HarnessSessionId, Chips, Files("../outside.ts"));

        Failure(result, CanvasErrorKind.Invalid).ShouldBe("A file's path stays inside the mod's folder.");
        _events.Broadcasts.ShouldBeEmpty();
    }

    // ── fleet_mod_check ─────────────────────────────────────────────────

    [Fact]
    public async Task Check_shows_the_report_the_load_problem_and_the_last_twenty_log_lines()
    {
        SeedDraft();
        var report = Json("""{"ok":true,"name":"test-chips","version":"0.1.0"}""");
        _checker.Report = report;
        _runner.Problem = new ModDraftProblem("Unexpected token at line 3", null, _time.GetUtcNow());
        for (var i = 1; i <= 25; i++)
            _runner.AddLog(i % 2 == 0 ? "warn" : "log", $"line {i}");

        var result = await Call("check");

        result.Value!.Title.ShouldBe($"Checked {Chips}");
        var lines = result.Value!.Output.Split('\n');
        result.Value!.Output.ShouldStartWith(ModCheckText.Format(report));
        result.Value!.Output.ShouldContain("\nLast load: Unexpected token at line 3\nLog:\n");
        result.Value!.Output.ShouldNotContain("line 5\n");
        lines[^20].ShouldBe("09:05:05 warn line 6");
        lines[^1].ShouldBe("09:05:24 log line 25");
    }

    [Fact]
    public async Task Check_with_no_log_says_so_and_has_no_load_line()
    {
        SeedDraft();

        var result = await Call("check");

        result.Value!.Output.ShouldEndWith("\nLog: (empty)");
        result.Value!.Output.ShouldNotContain("Last load");
    }

    // ── fleet_mod_reload ────────────────────────────────────────────────

    [Fact]
    public async Task Reload_says_it_loaded_and_what_it_registered()
    {
        SeedDraft();

        var result = await Call("reload");

        result.Value!.Title.ShouldBe($"Reloaded {Chips}");
        result.Value!.Output.ShouldBe($"{Chips} loaded in this session.\nHooks:\n  session.start\n  ui.render \"ToolUse\" {{ props: {{ tool: \"bash\" }} }}");
        _runner.Reloaded.ShouldHaveSingleItem().ShouldBe((Owner, SessionId, Chips));
    }

    [Fact]
    public async Task Reload_that_is_refused_is_still_ok_and_shows_the_reason_and_check()
    {
        SeedDraft();
        var report = Json("""{"ok":false,"errors":[{"code":"parse","message":"Unexpected token","line":3}]}""");
        _runner.Load = new ModDraftLoad(false, "Unexpected token", report, default);

        var result = await Call("reload");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Title.ShouldBe($"{Chips} didn't load");
        result.Value!.Output.ShouldBe($"{Chips} didn't load: Unexpected token\n{ModCheckText.Format(report)}");
    }

    [Fact]
    public async Task Reload_that_is_refused_without_a_report_is_just_the_reason()
    {
        SeedDraft();
        _runner.Load = new ModDraftLoad(false, "The module is missing.", null, default);

        (await Call("reload")).Value!.Output.ShouldBe($"{Chips} didn't load: The module is missing.");
    }

    [Fact]
    public async Task Reload_turns_a_draft_that_three_failures_turned_off_back_on_and_says_so()
    {
        SeedDraft(off: new ModOff(ModOffBy.Strikes, _time.GetUtcNow(), "boom"));

        var result = await Call("reload");

        result.Value!.Output.ShouldEndWith("It was off after three failures; it's on again.");
        (await _store.GetDraftAsync(Owner, SessionId, Chips))!.Off.ShouldBeNull();
        _runner.Reloaded.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reload_turns_a_draft_the_user_turned_off_back_on_too()
    {
        SeedDraft(off: new ModOff(ModOffBy.User, _time.GetUtcNow()));

        var result = await Call("reload");

        result.Value!.Output.ShouldEndWith("It was turned off; it's on again.");
        (await _store.GetDraftAsync(Owner, SessionId, Chips))!.Off.ShouldBeNull();
    }

    // ── fleet_mod_test ──────────────────────────────────────────────────

    [Fact]
    public async Task Test_with_an_e_that_isnt_an_object_is_invalid()
    {
        SeedDraft();

        var result = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("[1]"));

        Failure(result, CanvasErrorKind.Invalid).ShouldBe("e must be an object with the event's fields.");
        _runner.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_of_ui_render_needs_a_known_component()
    {
        SeedDraft();

        Failure(await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("{}")), CanvasErrorKind.Invalid)
            .ShouldStartWith("A ui.render test needs a component");
        Failure(await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"Sparkle"}""")), CanvasErrorKind.Invalid)
            .ShouldStartWith("A ui.render test needs a component");
        _runner.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_with_an_unknown_event_is_invalid()
    {
        SeedDraft();

        Failure(await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.explode", Json("{}")), CanvasErrorKind.Invalid)
            .ShouldContain("ui.explode isn't an event to test");
    }

    [Fact]
    public async Task Test_of_a_tool_use_fills_in_what_a_real_render_has_and_this_sessions_id()
    {
        SeedDraft();

        await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"ToolUse","props":{"tool":"bash","input":{"command":"dotnet test"}}}"""));

        var (user, session, eventName, sent) = _runner.Dispatched.ShouldHaveSingleItem();
        (user, session, eventName).ShouldBe((Owner, SessionId, "ui.render"));
        sent.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        sent.GetProperty("component").GetString().ShouldBe("ToolUse");
        sent.GetProperty("requestId").GetString().ShouldStartWith("test-");
        var props = sent.GetProperty("props");
        props.GetProperty("tool").GetString().ShouldBe("bash");
        props.GetProperty("rawTool").GetString().ShouldBe("bash");
        props.GetProperty("category").GetString().ShouldBe("shell");
        props.GetProperty("status").GetString().ShouldBe("completed");
        props.GetProperty("title").GetString().ShouldBe("bash");
        props.GetProperty("input").GetProperty("command").GetString().ShouldBe("dotnet test");
        props.GetProperty("inputTruncated").GetBoolean().ShouldBeFalse();
        props.GetProperty("output").GetString().ShouldBe("");
        props.GetProperty("outputTruncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Test_keeps_what_the_agent_sent_and_calls_other_tools_other()
    {
        SeedDraft();

        await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json(
            """{"component":"ToolResult","requestId":"mine","sessionId":"liar","props":{"tool":"read","status":"error","output":"nope","input":{}}}"""));

        var sent = _runner.Dispatched.ShouldHaveSingleItem().E;
        sent.GetProperty("requestId").GetString().ShouldBe("mine");
        sent.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        var props = sent.GetProperty("props");
        props.GetProperty("category").GetString().ShouldBe("other");
        props.GetProperty("status").GetString().ShouldBe("error");
        props.GetProperty("output").GetString().ShouldBe("nope");
    }

    [Fact]
    public async Task Test_fills_in_the_other_components_defaults()
    {
        SeedDraft();

        await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"ComposerBand"}"""));
        await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"Pane"}"""));

        _runner.Dispatched[0].E.GetProperty("props").GetProperty("isWorking").GetBoolean().ShouldBeFalse();
        _runner.Dispatched[1].E.GetProperty("props").GetProperty("title").GetString().ShouldBe("");
    }

    [Fact]
    public async Task Test_of_a_control_event_sends_the_fields_with_the_session_id()
    {
        SeedDraft();

        await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.press", Json("""{"handle":"h1"}"""));

        var sent = _runner.Dispatched.ShouldHaveSingleItem().E;
        sent.GetProperty("handle").GetString().ShouldBe("h1");
        sent.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        sent.TryGetProperty("props", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Test_that_no_hook_matched_says_how_to_check_the_matcher()
    {
        SeedDraft();
        _runner.Run = new ModDraftTestRun(false, null, [], []);

        var result = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"Pane"}"""));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Output.ShouldBe(
            "No hook in this session's mods matched ui.render Pane. Check the matcher: props.tool is Fleet's name for the tool "
            + "(bash, read, edit…), and the draft must have loaded (fleet_mod_reload).");

        var other = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "session.start", Json("{}"));
        other.Value!.Output.ShouldStartWith("No hook in this session's mods matched session.start. Check the matcher");
    }

    [Fact]
    public async Task Test_that_was_dispatched_shows_the_result_who_drew_it_failures_and_the_new_log_lines()
    {
        SeedDraft();
        _runner.AddLog("log", "old line");
        _runner.OnDispatch = () => _runner.AddLog("log", "drew a pill");
        _runner.Run = new ModDraftTestRun(
            true,
            Json("""{"type":"Pill","text":"3 passed"}"""),
            ["test-chips", "other-mod"],
            [new ModDraftFailure("test-chips", "ui.render", "throw", "boom", 2)]);

        var result = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "ui.render", Json("""{"component":"Pane"}"""));

        result.Value!.Title.ShouldBe($"Tested {Chips}: ui.render");
        var output = result.Value!.Output;
        output.ShouldStartWith("Drawn by: test-chips, other-mod\nResult:\n{");
        output.ShouldContain("  \"type\": \"Pill\"");
        output.ShouldContain("Failures:\ntest-chips throw: boom (2 in a row)");
        output.ShouldEndWith("Log:\n09:05:01 log drew a pill");
        output.ShouldNotContain("old line");
    }

    [Fact]
    public async Task Test_of_other_events_doesnt_say_who_drew_it()
    {
        SeedDraft();

        var result = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "session.start", Json("{}"));

        result.Value!.Output.ShouldStartWith("Result:");
        result.Value!.Output.ShouldNotContain("Drawn by");
    }

    [Fact]
    public async Task Test_cuts_a_result_longer_than_sixteen_thousand_characters()
    {
        SeedDraft();
        _runner.Run = new ModDraftTestRun(true, Json($$"""{"text":"{{new string('x', 20_000)}}"}"""), [], []);

        var result = await _bridge.TestAsync(Token, HarnessSessionId, Chips, "session.start", Json("{}"));

        var output = result.Value!.Output;
        output.ShouldContain("… (cut)");
        output.Length.ShouldBeLessThan(16_100);
    }

    // ── fleet_mod_keep ──────────────────────────────────────────────────

    [Fact]
    public async Task Keep_that_fails_the_check_says_what_to_fix_and_doesnt_ask()
    {
        SeedDraft();
        var report = Json("""{"ok":false,"errors":[{"code":"parse","message":"Unexpected token","line":3}]}""");
        _checker.Report = report;

        var result = await Call("keep");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Title.ShouldBe($"{Chips} can't be kept yet");
        result.Value!.Output.ShouldBe($"The check found problems; fix them first.\n{ModCheckText.Format(report)}");
        _keepRequests.Get(Owner, SessionId, Chips).ShouldBeNull();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_returns_the_version_when_the_user_keeps_it_within_the_wait()
    {
        SeedDraft();

        var waiting = Call("keep");
        await WaitForRequestAsync();
        (await _service.KeepAsync(SessionId, Chips, "from the card")).IsSuccess.ShouldBeTrue();
        var result = await waiting;

        result.Value!.Title.ShouldBe($"Kept {Chips}");
        result.Value!.Output.ShouldBe($"The user kept {Chips} as v1. It runs in all their sessions now.");
        _keepRequests.Get(Owner, SessionId, Chips).ShouldBeNull();
    }

    [Fact]
    public async Task Keep_says_declined_when_the_user_turns_the_draft_off()
    {
        SeedDraft();

        var waiting = Call("keep");
        await WaitForRequestAsync();
        await _service.SetDraftOnAsync(SessionId, Chips, on: false);
        var result = await waiting;

        result.Value!.Title.ShouldBe($"{Chips} not kept");
        result.Value!.Output.ShouldBe($"The user didn't keep {Chips}. The draft stays in this session.");
    }

    [Fact]
    public async Task Keep_says_declined_when_the_user_declines_the_request()
    {
        SeedDraft();

        var waiting = Call("keep");
        await WaitForRequestAsync();
        (await _service.DeclineKeepAsync(SessionId, Chips)).IsSuccess.ShouldBeTrue();
        var result = await waiting;

        result.Value!.Title.ShouldBe($"{Chips} not kept");
        _events.Broadcasts.Select(b => b.DomainEvent.ShouldBeOfType<ModsChanged>().Payload.Reason).ShouldBe(["keep-requested", "keep-declined"]);
    }

    [Fact]
    public async Task Keep_tells_the_agent_not_to_ask_again_when_the_wait_runs_out_and_leaves_the_request_up()
    {
        SeedDraft();

        var waiting = Call("keep");
        await WaitForRequestAsync();
        _time.Advance(TimeSpan.FromSeconds(ModBridge.KeepWaitSeconds - 1));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(2));
        var result = await waiting;

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Title.ShouldBe($"Asked to keep {Chips}");
        result.Value!.Output.ShouldBe(
            "Waiting for the user: the request is on the draft card in the conversation. Don't ask again; they'll keep it or not from there.");
        _keepRequests.Get(Owner, SessionId, Chips).ShouldNotBeNull();
    }

    [Fact]
    public async Task Keep_records_the_note_and_raises_keep_requested()
    {
        SeedDraft();

        var waiting = _bridge.KeepAsync(Token, HarnessSessionId, Chips, "Shows the test totals");
        await WaitForRequestAsync();

        _keepRequests.Get(Owner, SessionId, Chips)!.Note.ShouldBe("Shows the test totals");
        var sent = _events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe(("sessions", "mods.changed", Owner));
        var payload = sent.DomainEvent.ShouldBeOfType<ModsChanged>().Payload;
        (payload.Reason, payload.Name, payload.SessionId).ShouldBe(("keep-requested", Chips, SessionId));

        _time.Advance(TimeSpan.FromSeconds(ModBridge.KeepWaitSeconds + 1));
        await waiting;
    }

    [Fact]
    public async Task A_second_keep_replaces_the_first_request()
    {
        SeedDraft();

        var first = _bridge.KeepAsync(Token, HarnessSessionId, Chips, "first");
        await WaitForRequestAsync();
        var second = _bridge.KeepAsync(Token, HarnessSessionId, Chips, "second");
        var firstResult = await first;

        firstResult.Value!.Title.ShouldBe($"Asked to keep {Chips}");
        _keepRequests.Get(Owner, SessionId, Chips)!.Note.ShouldBe("second");
        second.IsCompleted.ShouldBeFalse();

        await _service.DeclineKeepAsync(SessionId, Chips);
        (await second).Value!.Title.ShouldBe($"{Chips} not kept");
    }

    // ── fleet_mod_list ──────────────────────────────────────────────────

    [Fact]
    public async Task List_with_nothing_says_none_twice()
    {
        var result = await Call("list");

        result.Value!.Title.ShouldBe("Mods");
        result.Value!.Output.ShouldBe("Drafts in this session:\n(none)\nKept:\n(none)");
    }

    [Fact]
    public async Task List_shows_each_draft_with_its_state_and_each_kept_mod()
    {
        SeedDraft("alpha");
        SeedDraft("beta", off: new ModOff(ModOffBy.Strikes, _time.GetUtcNow(), "boom"));
        SeedDraft("gamma", off: new ModOff(ModOffBy.User, _time.GetUtcNow()));
        _store.SeedVersion(Owner, "kept-one", 2, "0.2.0");
        _store.SeedHistory(Owner, new ModHistory("kept-one", 2, null, [
            new ModVersion(1, _time.GetUtcNow(), "0.1.0", "sha", SessionId, null, null, null),
            new ModVersion(2, _time.GetUtcNow(), "0.2.0", "sha", SessionId, null, null, null)]));
        _store.SeedHistory(Owner, new ModHistory("kept-two", 1, new ModOff(ModOffBy.User, _time.GetUtcNow()), [
            new ModVersion(1, _time.GetUtcNow(), "1.0.0", "sha", SessionId, null, null, null)]));

        var output = (await Call("list")).Value!.Output.Split('\n');

        output[0].ShouldBe("Drafts in this session:");
        output[1].ShouldBe("alpha 0.1.0 · on · loaded");
        output[2].ShouldBe("beta 0.1.0 · off: turned off after three failures in a row · not loaded");
        output[3].ShouldBe("gamma 0.1.0 · off: turned off by the user · not loaded");
        output[4].ShouldBe("Kept:");
        output[5].ShouldBe("kept-one v2 (0.2.0) · on");
        output[6].ShouldBe("kept-two v1 (1.0.0) · off");
    }

    [Fact]
    public async Task List_says_a_draft_didnt_load_and_why()
    {
        SeedDraft();
        _runner.Problem = new ModDraftProblem("Unexpected token", null, _time.GetUtcNow());

        (await Call("list")).Value!.Output.ShouldContain($"{Chips} 0.1.0 · on · didn't load: Unexpected token");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Waits until the keep tool has recorded its request, which is when it starts waiting for the user.</summary>
    private async Task WaitForRequestAsync()
    {
        for (var i = 0; i < 500 && _keepRequests.Get(Owner, SessionId, Chips) is null; i++)
            await Task.Delay(5);
        _keepRequests.Get(Owner, SessionId, Chips).ShouldNotBeNull();
        // The tool awaits the decision right after the request is recorded and the event raised.
        await Task.Delay(20);
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
