using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>Loading, dispatch, strikes, the host's own calls and the check, against a scripted host.</summary>
public sealed class ModHostLoadingTests : IDisposable
{
    private const string User = "test-user";
    private static readonly string[] Chips = ["test-chips@v1"];

    private readonly FakeFactory _factory = new();
    private readonly FakeGate _gate = new();
    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeStrikes _strikes = new();
    private readonly FakeUi _ui = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, string[]> _hooks = [];
    private readonly ModHostSupervisor _supervisor;

    public ModHostLoadingTests()
    {
        _supervisor = new ModHostSupervisor(User, new ModHostDependencies(
            new ModHostOptions(), _factory, _gate, new FakeBun(), new FakeFiles(), _store, _strikes, _ui,
            (_, id, _) => Task.FromResult(_sessions.GetValueOrDefault(id)), _time, NullLogger.Instance));
        // A load answers with the hooks the test gave the mod's name (ui.render and session.start unless it said otherwise).
        _factory.Answers["load"] = p => Answer(new
        {
            check = new { ok = true },
            hooks = (_hooks.GetValueOrDefault(p.GetProperty("name").GetString()!) ?? ["ui.render", "session.start"]).Select(e => new { @event = e }),
        });
        _factory.Answers["dispatch"] = _ => Answer(new { result = new { type = "Fleet" }, failures = Array.Empty<object>() });
    }

    public void Dispose() => _store.DeleteFolders();

    private static Task<JsonElement> Answer(object value) => Task.FromResult(JsonSerializer.SerializeToElement(value));

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private void Keep(string name, int active, params int[] earlier)
        => _store.SeedHistory(User, new ModHistory(name, active, null,
            [.. earlier.Append(active).Select(n => new ModVersion(n, DateTimeOffset.UnixEpoch, "0.1.0", "abc", null, null, null, null))]));

    private async Task<FakeConnection> StartedAsync()
    {
        await _supervisor.EnsureAsync(CancellationToken.None).Within();
        return await _factory.NextStart();
    }

    private Task<ModDispatchResult> Dispatch(string @event, string session, object? e = null)
        => _supervisor.DispatchAsync(new ModDispatchRequest(@event, session, Json(e ?? new { component = "ToolRow" })), CancellationToken.None).Within();

    private Task<JsonElement> Call(string method, object parameters)
        => ((IModHostCalls)_supervisor).HandleRequestAsync(method, Json(parameters), CancellationToken.None).Within();

    private Task<ModHostStatus> WaitFor(string state)
    {
        var seen = new TaskCompletionSource<ModHostStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _supervisor.Changed += s =>
        {
            if (s.State == state)
                seen.TrySetResult(s);
        };
        if (_supervisor.GetStatus().State == state)
            seen.TrySetResult(_supervisor.GetStatus());
        return seen.Task.Within();
    }

    private static string?[] Ids(IEnumerable<JsonElement> sent) => [.. sent.Select(p => p.GetProperty("id").GetString())];

    private static string?[] Chain(JsonElement dispatch) => [.. dispatch.GetProperty("mods").EnumerateArray().Select(m => m.GetString())];

    [Fact]
    public async Task Start_loads_kept_mods_then_drafts_from_a_staged_copy_named_after_the_mod()
    {
        Keep("test-chips", 2, 1);
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        var loads = (await StartedAsync()).Sent("load");

        loads.Length.ShouldBe(2);
        loads[0].GetRawText().ShouldBe(Json(new { id = "test-chips@v2", name = "test-chips", version = 2, root = _store.VersionFolder(User, "test-chips", 2) }).GetRawText());
        var staged = _store.StagedDrafts.ShouldHaveSingleItem();
        staged.Destination.ShouldStartWith(Path.Combine(_store.HostFolder(User), "drafts", "ses_test1") + Path.DirectorySeparatorChar);
        Path.GetFileName(staged.Destination).ShouldBe("demo-mod");
        loads[1].GetRawText().ShouldBe(Json(new { id = "demo-mod@draft:ses_test1", name = "demo-mod", version = "draft", sessionId = "ses_test1", root = staged.Destination }).GetRawText());
    }

    [Fact]
    public async Task Undo_unloads_the_newer_version_and_loads_the_older()
    {
        Keep("test-chips", 2, 1);
        var connection = await StartedAsync();
        Keep("test-chips", 1);
        await _supervisor.EnsureAsync(CancellationToken.None).Within();
        connection.Methods.ShouldBe(["load", "unload", "load"]);
        Ids(connection.Requests.Skip(1).Select(r => r.Params)).ShouldBe(["test-chips@v2", "test-chips@v1"]);
    }

    [Fact]
    public async Task A_refused_load_is_a_load_problem_and_is_not_retried_until_mods_change()
    {
        Keep("test-chips", 1);
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        _store.StageRefusal = "demo-mod has a link in it.";
        var report = Json(new { ok = false });
        _factory.Answers["load"] = _ => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.NotLoaded, "test-chips doesn't load: boom", report));
        var connection = await StartedAsync();

        var problem = _supervisor.GetLoadProblem("test-chips@v1").ShouldNotBeNull();
        problem.Message.ShouldBe("test-chips doesn't load: boom");
        problem.Report!.Value.GetRawText().ShouldBe(report.GetRawText());
        _supervisor.GetLoadProblem("demo-mod@draft:ses_test1").ShouldBe(new ModLoadProblem("demo-mod has a link in it.", null));
        await _supervisor.EnsureAsync(CancellationToken.None).Within();
        connection.Sent("load").Length.ShouldBe(1);

        _factory.Answers["load"] = _ => Answer(new { check = new { ok = true }, hooks = Array.Empty<object>() });
        _store.StageRefusal = null;
        _supervisor.ClearLoadProblems();
        await _supervisor.EnsureAsync(CancellationToken.None).Within();
        Ids(connection.Sent("load")).ShouldBe(["test-chips@v1", "test-chips@v1", "demo-mod@draft:ses_test1"]);
        _supervisor.GetLoadProblem("test-chips@v1").ShouldBeNull();
    }

    [Fact]
    public async Task Reloading_a_draft_stages_it_again_and_deletes_the_old_copy_only_after_the_host_took_it()
    {
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        var connection = await StartedAsync();
        var first = _store.StagedDrafts[0].Destination;

        await _supervisor.ReloadDraftAsync("ses_test1", "demo-mod", CancellationToken.None).Within();
        var second = _store.StagedDrafts[1].Destination;
        second.ShouldNotBe(first);
        connection.Sent("load")[1].GetProperty("root").GetString().ShouldBe(second);
        Directory.Exists(first).ShouldBeFalse();
        Directory.Exists(second).ShouldBeTrue();

        _factory.Answers["load"] = _ => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.NotLoaded, "demo-mod doesn't load: oops"));
        await _supervisor.ReloadDraftAsync("ses_test1", "demo-mod", CancellationToken.None).Within();
        Directory.Exists(second).ShouldBeTrue();
        Directory.Exists(_store.StagedDrafts[2].Destination).ShouldBeFalse();
        _supervisor.GetLoadProblem("demo-mod@draft:ses_test1")!.Message.ShouldBe("demo-mod doesn't load: oops");
        (await Dispatch("ui.render", "ses_test1")).Dispatched.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_up_only_for_checks_loads_nothing_and_stops_after_a_minute()
    {
        Keep("test-chips", 1);
        _gate.Safe = true;
        _factory.Answers["check"] = _ => Answer(new { ok = true });
        (await _supervisor.CheckAsync("/work/demo-mod", CancellationToken.None).Within()).GetProperty("ok").GetBoolean().ShouldBeTrue();
        var connection = await _factory.NextStart();
        connection.Methods.ShouldBe(["check"]);
        connection.Sent("check")[0].GetRawText().ShouldBe(Json(new { root = "/work/demo-mod", manifest = Path.Combine("/work/demo-mod", "mod.json") }).GetRawText());

        _time.Advance(TimeSpan.FromSeconds(59));
        _supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
        var stopped = WaitFor(ModHostStates.Stopped);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await stopped).Reason.ShouldBe("Started without mods.");
        connection.Methods.ShouldBe(["check"]);
        connection.Shutdowns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_check_with_the_switch_off_or_that_times_out_is_not_ready_and_a_timeout_restarts_the_host()
    {
        _gate.On = false;
        (await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.CheckAsync("/work/demo-mod", CancellationToken.None).Within())).Message.ShouldBe(ModsFeature.TurnedOffMessage);
        _factory.Launches.ShouldBeEmpty();

        _gate.On = true;
        _factory.Answers["check"] = _ => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.Internal, "the check broke"));
        (await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.CheckAsync("/work/demo-mod", CancellationToken.None).Within())).Message.ShouldBe("the check broke");
        _factory.Answers["check"] = _ => Task.FromException<JsonElement>(new TimeoutException());
        var restarting = WaitFor(ModHostStates.Restarting);
        await Should.ThrowAsync<ModHostNotReadyException>(_supervisor.CheckAsync("/work/demo-mod", CancellationToken.None).Within());
        (await _factory.NextStart()).Kills.ShouldBe(1);
        await restarting;
    }

    [Fact]
    public async Task Dispatch_sends_the_chain_and_returns_the_hosts_result()
    {
        (await Dispatch("ui.render", "ses_test1")).ShouldBe(ModDispatchResult.NotDispatched);
        Keep("test-chips", 1);
        var connection = await StartedAsync();
        _factory.Answers["dispatch"] = _ => Answer(new { result = new { type = "Text" }, drawnBy = Chips, failures = Array.Empty<object>() });

        var result = await Dispatch("ui.render", "ses_test1");
        result.Dispatched.ShouldBeTrue();
        result.Result!.Value.GetProperty("type").GetString().ShouldBe("Text");
        result.DrawnBy.ShouldBe(["test-chips@v1"]);
        connection.Sent("dispatch")[0].GetRawText().ShouldBe(Json(new { @event = "ui.render", sessionId = "ses_test1", e = new { component = "ToolRow" }, mods = Chips }).GetRawText());
    }

    [Fact]
    public async Task An_unhooked_event_never_crosses_a_control_event_always_does_and_an_expired_handle_is_not_dispatched()
    {
        _hooks["test-chips"] = ["ui.render"];
        Keep("test-chips", 1);
        var connection = await StartedAsync();
        (await Dispatch("turn.complete", "ses_test1")).ShouldBe(ModDispatchResult.NotDispatched);
        connection.Sent("dispatch").ShouldBeEmpty();

        (await _supervisor.DispatchAsync(new ModDispatchRequest("ui.press", "ses_test1", Json(new { handle = "h1" }), "phone"), CancellationToken.None).Within()).Dispatched.ShouldBeTrue();
        var press = connection.Sent("dispatch").ShouldHaveSingleItem();
        press.GetProperty("surface").GetString().ShouldBe("phone");

        _factory.Answers["dispatch"] = _ => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.InvalidParams, "unknown handle h1"));
        (await Dispatch("ui.press", "ses_test1", new { handle = "h1" })).ShouldBe(ModDispatchResult.NotDispatched);
    }

    [Fact]
    public async Task A_draft_only_gets_its_own_sessions_events()
    {
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        var connection = await StartedAsync();
        (await Dispatch("ui.render", "ses_test2")).ShouldBe(ModDispatchResult.NotDispatched);
        (await Dispatch("ui.render", "ses_test1")).Dispatched.ShouldBeTrue();
        Chain(connection.Sent("dispatch").ShouldHaveSingleItem()).ShouldBe(["demo-mod@draft:ses_test1"]);
    }

    [Fact]
    public async Task After_a_restart_a_sessions_reload_start_goes_before_its_held_dispatches()
    {
        Keep("test-chips", 1);
        var connection = await StartedAsync();
        await Dispatch("ui.render", "ses_test1");
        var loadAnswer = _factory.Answers["load"];
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Answers["load"] = async p =>
        {
            await hold.Task;
            return await loadAnswer(p);
        };

        connection.Crash();
        await WaitFor(ModHostStates.Restarting);
        _time.Advance(TimeSpan.FromSeconds(0.5));
        var restarted = await _factory.NextStart();
        var render = Dispatch("ui.render", "ses_test1");
        restarted.Methods.ShouldBe(["load"]);
        hold.SetResult();
        (await render).Dispatched.ShouldBeTrue();

        restarted.Methods.ShouldBe(["load", "dispatch", "dispatch"]);
        var dispatches = restarted.Sent("dispatch");
        dispatches[0].GetProperty("event").GetString().ShouldBe("session.start");
        dispatches[0].GetProperty("e").GetRawText().ShouldBe(Json(new { sessionId = "ses_test1", reason = "reload" }).GetRawText());
        Chain(dispatches[0]).ShouldBe(["test-chips@v1"]);
        dispatches[1].GetProperty("event").GetString().ShouldBe("ui.render");
    }

    [Fact]
    public async Task Three_strikes_turn_a_current_mod_off_once_and_other_ids_are_ignored()
    {
        Keep("test-chips", 1);
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        await StartedAsync();
        _factory.Answers["dispatch"] = _ => Answer(new
        {
            result = new { type = "Fleet" },
            failures = new object[]
            {
                new { mod = "demo-mod@draft:ses_test1", strikes = 2, message = "not yet" },
                new { mod = "test-chips@v1", strikes = 3, message = "boom" },
                new { mod = "test-chips@v1", strikes = 4, message = "again" },
                new { mod = "test-chips@v9", strikes = 3, message = "stale" },
            },
        });
        await Dispatch("ui.render", "ses_test1");
        _strikes.Recorded.ShouldBe(["kept test-chips: boom"]);

        var calls = (IModHostCalls)_supervisor;
        calls.HandleNotification("failed", Json(new { mod = "test-chips@v1", strikes = 5, message = "late" }));
        calls.HandleNotification("failed", Json(new { mod = "demo-mod@draft:ses_test1", strikes = 3, message = "bad", sessionId = "ses_test1" }));
        _strikes.Recorded.ShouldBe(["kept test-chips: boom", "draft ses_test1/demo-mod: bad"]);
        (await Dispatch("ui.render", "ses_test2")).ShouldBe(ModDispatchResult.NotDispatched);
    }

    [Fact]
    public async Task Store_calls_keep_values_under_the_mods_name_and_a_draft_shares_its_kept_mods_store()
    {
        Keep("test-chips", 1);
        _store.SeedDraft(User, "ses_test1", "test-chips");
        await StartedAsync();
        await Call("store.set", new { mod = "test-chips@v1", sessionId = "ses_test2", key = "count", value = 3 });
        (await Call("store.get", new { mod = "test-chips@draft:ses_test1", sessionId = "ses_test1", key = "count" })).GetRawText().ShouldBe("""{"value":3}""");
        (await Call("store.keys", new { mod = "test-chips@v1", sessionId = "ses_test2" })).GetRawText().ShouldBe("""{"keys":["count"]}""");
        await Call("store.delete", new { mod = "test-chips@v1", sessionId = "ses_test2", key = "count" });
        (await Call("store.get", new { mod = "test-chips@v1", sessionId = "ses_test2", key = "count" })).GetRawText().ShouldBe("{}");

        (await Should.ThrowAsync<ModHostRpcException>(Call("store.set", new { mod = "test-chips@v1", sessionId = "ses_test2", key = "no good", value = 1 }))).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Session_get_answers_only_for_the_users_own_sessions()
    {
        Keep("test-chips", 1);
        await StartedAsync();
        _sessions["ses_test1"] = new Session { Id = "ses_test1", Title = "Demo session", HarnessType = "opencode", Directory = "/work/demo", UserId = User };
        _sessions["ses_test2"] = new Session { Id = "ses_test2", Title = "Someone else's", UserId = "test-other" };

        (await Call("session.get", new { mod = "test-chips@v1", sessionId = "ses_test1" })).GetRawText()
            .ShouldBe("""{"id":"ses_test1","title":"Demo session","harness":"opencode","cwd":"/work/demo","surfaces":["desktop"]}""");
        foreach (var session in new[] { "ses_test2", "ses_test3" })
            (await Should.ThrowAsync<ModHostRpcException>(Call("session.get", new { mod = "test-chips@v1", sessionId = session }))).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Calls_refuse_unknown_mods_drafts_from_other_sessions_and_unknown_methods()
    {
        _store.SeedDraft(User, "ses_test1", "demo-mod");
        await StartedAsync();
        (await Should.ThrowAsync<ModHostRpcException>(Call("store.keys", new { mod = "other-mod@v1", sessionId = "ses_test1" }))).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
        (await Should.ThrowAsync<ModHostRpcException>(Call("store.keys", new { mod = "demo-mod@draft:ses_test1", sessionId = "ses_test2" }))).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
        (await Should.ThrowAsync<ModHostRpcException>(Call("ui.set", new { mod = "demo-mod@draft:ses_test1", sessionId = "ses_test1" }))).Code.ShouldBe(ModHostErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Ui_calls_and_notifications_go_to_the_ui()
    {
        Keep("test-chips", 1);
        await StartedAsync();
        await Call("ui.open", new { mod = "test-chips@v1", sessionId = "ses_test1", id = "panel", title = "Chips" });
        await Call("ui.close", new { mod = "test-chips@v1", sessionId = "ses_test1", id = "panel" });
        await Call("ui.toast", new { mod = "test-chips@v1", sessionId = "ses_test1", text = "Done", timeoutMs = 3000, tone = "warn" });
        var calls = (IModHostCalls)_supervisor;
        calls.HandleNotification("invalidate", Json(new { mod = "test-chips@v1" }));
        calls.HandleNotification("log", Json(new { mod = "test-chips@v1", sessionId = "ses_test1", level = "warn", text = "careful" }));
        _ui.Calls.ShouldBe([
            "open test-chips@v1 ses_test1 panel Chips", "close test-chips@v1 ses_test1 panel", "toast test-chips@v1 ses_test1 Done 3000 warn",
            "invalidate test-chips@v1 ", "log test-chips@v1 ses_test1 warn careful",
        ]);
    }

    [Fact]
    public async Task Forget_tells_the_host()
    {
        Keep("test-chips", 1);
        var connection = await StartedAsync();
        await _supervisor.ForgetSessionAsync("ses_test1").Within();
        connection.Sent("forget").ShouldHaveSingleItem().GetRawText().ShouldBe("""{"sessionId":"ses_test1"}""");
    }
}
