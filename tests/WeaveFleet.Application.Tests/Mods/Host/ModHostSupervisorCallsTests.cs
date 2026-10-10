using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>What the host asks of Fleet: <c>$</c> calls and notifications.</summary>
public sealed class ModHostSupervisorCallsTests : ModHostSupervisorTestBase
{
    private Task<JsonElement> Call(string method, string parameters)
        => Supervisor.HandleRequestAsync(method, ModHostTests.Json(parameters), CancellationToken.None).Within();

    private async Task<ModHostRpcException> Refused(string method, string parameters)
        => await Should.ThrowAsync<ModHostRpcException>(() => Call(method, parameters));

    private async Task RunningWithChipsAndADraftAsync()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Chips);
        Rig.Session(S2);
        await StartedAsync();
    }

    // ── $.store ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_calls_go_to_the_mods_store_which_a_draft_shares()
    {
        await RunningWithChipsAndADraftAsync();

        (await Call("store.set", """{ "mod": "test-chips@draft:ses_test1", "sessionId": "ses_test1", "key": "runs", "value": { "n": 3 } }"""))
            .EnumerateObject().ShouldBeEmpty();
        (await Call("store.set", """{ "mod": "test-chips@v1", "sessionId": "ses_test2", "key": "last", "value": "green" }""")).EnumerateObject().ShouldBeEmpty();

        var got = await Call("store.get", """{ "mod": "test-chips@v1", "sessionId": "ses_test2", "key": "runs" }""");
        got.GetProperty("value").GetProperty("n").GetInt32().ShouldBe(3);
        (await Rig.Store.GetValueAsync(User, Chips, "runs")).ShouldNotBeNull();
        var keys = await Call("store.keys", """{ "mod": "test-chips@v1", "sessionId": "ses_test2" }""");
        keys.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ShouldBe(["last", "runs"]);

        (await Call("store.delete", """{ "mod": "test-chips@v1", "sessionId": "ses_test2", "key": "runs" }""")).EnumerateObject().ShouldBeEmpty();
        var missing = await Call("store.get", """{ "mod": "test-chips@v1", "sessionId": "ses_test2", "key": "runs" }""");
        missing.TryGetProperty("value", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("store.get", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "key": "a/b" }""")]
    [InlineData("store.set", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "key": "", "value": 1 }""")]
    [InlineData("store.delete", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "key": "../x" }""")]
    [InlineData("store.set", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "key": "ok" }""")]
    public async Task A_bad_store_call_is_invalid_params(string method, string parameters)
    {
        await RunningWithChipsAndADraftAsync();

        (await Refused(method, parameters)).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task A_full_store_is_invalid_params_with_the_message()
    {
        await RunningWithChipsAndADraftAsync();
        Rig.Store.StoreFull = true;

        var refused = await Refused("store.set", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "key": "big", "value": "x" }""");

        refused.Code.ShouldBe(ModHostErrorCodes.InvalidParams);
        refused.Message.ShouldBe("The store of test-chips is full.");
    }

    // ── Who may call ────────────────────────────────────────────────────

    [Fact]
    public async Task A_mod_that_isnt_loaded_is_refused()
    {
        await RunningWithChipsAndADraftAsync();

        (await Refused("store.keys", """{ "mod": "test-chips@v2", "sessionId": "ses_test1" }""")).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
        (await Refused("store.keys", """{ "sessionId": "ses_test1" }""")).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task A_draft_calling_from_another_session_is_refused()
    {
        await RunningWithChipsAndADraftAsync();

        (await Refused("store.keys", """{ "mod": "test-chips@draft:ses_test1", "sessionId": "ses_test2" }""")).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
        (await Refused("session.get", """{ "mod": "test-chips@draft:ses_test1", "sessionId": "ses_test2" }""")).Code.ShouldBe(ModHostErrorCodes.InvalidParams);
    }

    // ── $.session ───────────────────────────────────────────────────────

    [Fact]
    public async Task Session_get_answers_from_the_users_session()
    {
        await RunningWithChipsAndADraftAsync();
        Rig.Ui.Surfaces.Add("phone");

        var session = await Call("session.get", """{ "mod": "test-chips@v1", "sessionId": "ses_test2" }""");

        session.GetProperty("id").GetString().ShouldBe(S2);
        session.GetProperty("title").GetString().ShouldBe("Title of ses_test2");
        session.GetProperty("harness").GetString().ShouldBe("opencode");
        session.GetProperty("cwd").GetString().ShouldBe("/work/ses_test2");
        session.GetProperty("surfaces").EnumerateArray().Select(s => s.GetString()).ShouldBe(["desktop", "phone"]);
    }

    [Fact]
    public async Task Session_get_refuses_a_session_that_is_missing_or_someone_elses()
    {
        await RunningWithChipsAndADraftAsync();
        Rig.Sessions.Seed(new Session { Id = "ses_other", UserId = "other-user", Title = "Not yours" });

        var missing = await Refused("session.get", """{ "mod": "test-chips@v1", "sessionId": "ses_nope" }""");
        var others = await Refused("session.get", """{ "mod": "test-chips@v1", "sessionId": "ses_other" }""");

        (missing.Code, missing.Message).ShouldBe((ModHostErrorCodes.InvalidParams, "unknown session"));
        (others.Code, others.Message).ShouldBe((ModHostErrorCodes.InvalidParams, "unknown session"));
    }

    // ── $.ui ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ui_calls_reach_the_browser_side()
    {
        await RunningWithChipsAndADraftAsync();

        (await Call("ui.open", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "id": "results", "title": "Test results" }""")).EnumerateObject().ShouldBeEmpty();
        (await Call("ui.open", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "id": "plain" }""")).EnumerateObject().ShouldBeEmpty();
        (await Call("ui.close", """{ "mod": "test-chips@v1", "sessionId": "ses_test1", "id": "results" }""")).EnumerateObject().ShouldBeEmpty();
        (await Call("ui.toast", """{ "mod": "test-chips@draft:ses_test1", "sessionId": "ses_test1", "text": "3 failed", "timeoutMs": 4000, "tone": "warn" }""")).EnumerateObject().ShouldBeEmpty();
        (await Call("ui.toast", """{ "mod": "test-chips@v1", "sessionId": "ses_test2", "text": "done" }""")).EnumerateObject().ShouldBeEmpty();

        Rig.Ui.Calls.ShouldBe([
            "open test-user test-chips@v1 ses_test1 results Test results",
            "open test-user test-chips@v1 ses_test1 plain ",
            "close test-user test-chips@v1 ses_test1 results",
            "toast test-user test-chips@draft:ses_test1 ses_test1 3 failed 4000 warn",
            "toast test-user test-chips@v1 ses_test2 done  ",
        ]);
    }

    [Fact]
    public async Task An_unknown_method_is_method_not_found()
    {
        await RunningWithChipsAndADraftAsync();

        var refused = await Refused("fs.read", """{ "mod": "test-chips@v1", "sessionId": "ses_test1" }""");

        (refused.Code, refused.Message).ShouldBe((ModHostErrorCodes.MethodNotFound, "method not found: fs.read"));
    }

    // ── Notifications ───────────────────────────────────────────────────

    [Fact]
    public async Task Invalidate_reaches_the_signals()
    {
        await RunningWithChipsAndADraftAsync();

        Supervisor.HandleNotification("invalidate", ModHostTests.Json("""{ "mod": "test-chips@v1", "sessionId": "ses_test1" }"""));
        Supervisor.HandleNotification("invalidate", ModHostTests.Json("""{ "mod": "test-chips@v1" }"""));

        Rig.Signals.Signals.ShouldBe(["invalidated test-user test-chips@v1 ses_test1", "invalidated test-user test-chips@v1 "]);
    }

    [Fact]
    public async Task Log_goes_to_the_mods_log()
    {
        await RunningWithChipsAndADraftAsync();

        Supervisor.HandleNotification("log", ModHostTests.Json("""{ "mod": "test-chips@v1", "sessionId": "ses_test1", "level": "warn", "text": "slow run" }"""));
        Supervisor.HandleNotification("log", ModHostTests.Json("""{ "mod": "test-chips@v1", "level": "info", "text": "loaded" }"""));

        Rig.Log.Read(User, Kept(Chips, 1)).ShouldBe([
            new ModLogLine(Rig.Time.GetUtcNow(), "warn", "slow run", S1),
            new ModLogLine(Rig.Time.GetUtcNow(), "info", "loaded", null),
        ]);
    }

    [Theory]
    [InlineData("log", "[]")]
    [InlineData("log", """{ "mod": 3 }""")]
    [InlineData("failed", """{ "mod": "test-chips@v1" }""")]
    [InlineData("invalidate", "null")]
    [InlineData("running", """{ "event": "turn.complete" }""")]
    [InlineData("something-new", "{}")]
    public async Task A_notification_Fleet_cant_read_is_dropped_without_throwing(string method, string parameters)
    {
        await RunningWithChipsAndADraftAsync();

        Should.NotThrow(() => Supervisor.HandleNotification(method, ModHostTests.Json(parameters)));
    }
}
