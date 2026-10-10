using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>Running events through the host: chains, failures and strikes, hangs, crashes and restarts.</summary>
public sealed class ModHostSupervisorDispatchTests : ModHostSupervisorTestBase
{
    private static ModWireDispatchResult Answer(string result = "null", IReadOnlyList<string>? drawnBy = null, params ModHookFailure[] failures)
        => new(ModHostTests.Json(result), drawnBy, failures);

    private static ModHookFailure Failure(string mod, int strikes, string message = "boom", string @event = "turn.complete")
        => new(mod, @event, "throw", message, strikes);

    // ── Chains ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dispatch_sends_the_chain_and_answers_with_the_result()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer("""{ "type": "Pill", "props": { "label": "3 failed" } }""", [Kept(Chips, 1)]));

        var result = await Dispatch("ui.render", S1, """{ "component": "StatusChip" }""").Within();

        result.Dispatched.ShouldBeTrue();
        result.Result.ShouldNotBeNull().GetProperty("type").GetString().ShouldBe("Pill");
        result.DrawnBy.ShouldBe([Kept(Chips, 1)]);
        result.Failures.ShouldBeEmpty();
        var sent = Factory.Current.Dispatches.ShouldHaveSingleItem();
        sent.ShouldSatisfyAllConditions(
            d => d.Event.ShouldBe("ui.render"),
            d => d.SessionId.ShouldBe(S1),
            d => d.E.GetProperty("component").GetString().ShouldBe("StatusChip"),
            d => d.Mods.ShouldBe([Kept(Demo, 1), Kept(Chips, 1)]),
            d => d.Surface.ShouldBeNull());
        Factory.Current.DispatchTimeouts.ShouldBe([TimeSpan.FromSeconds(15)]);
    }

    [Fact]
    public async Task An_event_no_mod_hooks_doesnt_cross_the_pipe()
    {
        Factory.Hooks[Chips] = [new("ui.render", null)];
        Rig.Keep(Chips);
        await StartedAsync();

        var result = await Dispatch("turn.complete", S1).Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        Factory.Current.Dispatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_control_event_is_sent_even_when_no_mod_hooks_it()
    {
        Factory.Hooks[Chips] = [new("ui.render", null)];
        Rig.Keep(Chips);
        await StartedAsync();

        var result = await Dispatch("ui.press", S1, """{ "handle": "h17" }""", "phone").Within();

        result.Dispatched.ShouldBeTrue();
        var sent = Factory.Current.Dispatches.ShouldHaveSingleItem();
        sent.Mods.ShouldBeEmpty();
        sent.Surface.ShouldBe("phone");
        sent.E.GetProperty("handle").GetString().ShouldBe("h17");
    }

    [Fact]
    public async Task A_draft_only_ever_gets_its_own_sessions_events()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        Rig.Draft(S1, Chips);
        Rig.Session(S2);
        await StartedAsync();

        await Dispatch("turn.complete", S2).Within();
        await Dispatch("turn.complete", S1).Within();

        var sent = Factory.Current.Dispatches;
        sent[0].Mods.ShouldBe([Kept(Demo, 1), Kept(Chips, 1)]);
        sent[1].Mods.ShouldBe([Kept(Demo, 1), DraftId(Chips, S1)]);
    }

    [Fact]
    public async Task Nothing_is_dispatched_while_the_host_is_stopped()
    {
        var result = await Dispatch("turn.complete", S1).Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        Factory.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dispatch_while_the_host_starts_waits_for_it()
    {
        Rig.Keep(Chips);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.BeforeStart = _ => release.Task;
        var starting = Supervisor.EnsureAsync();
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Starting, "the host is starting");

        var dispatch = Dispatch("turn.complete", S1);
        await Task.Delay(50);
        dispatch.IsCompleted.ShouldBeFalse();
        release.SetResult();

        (await dispatch.Within()).Dispatched.ShouldBeTrue();
        await starting.Within();
    }

    [Fact]
    public async Task A_dispatch_gives_up_on_a_start_after_five_seconds()
    {
        Rig.Keep(Chips);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.BeforeStart = _ => release.Task;
        var starting = Supervisor.EnsureAsync();
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Starting, "the host is starting");
        var before = Rig.Time.GetUtcNow();

        var result = await ModHostTests.AdvanceUntil(Rig.Time, Dispatch("turn.complete", S1), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));

        result.ShouldBe(ModDispatchResult.NotDispatched);
        (Rig.Time.GetUtcNow() - before).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        release.SetResult();
        await starting.Within();
    }

    [Fact]
    public async Task An_invalid_params_answer_is_not_dispatched_and_not_a_strike()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 1)));
        await Dispatch("turn.complete", S1).Within();

        Factory.OnDispatch = (_, _, _) => throw new ModHostRpcException(ModHostErrorCodes.InvalidParams, "unknown or expired handle");
        var result = await Dispatch("ui.press", S1, """{ "handle": "h9" }""").Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        Factory.Current.Kills.ShouldBe(0);
    }

    [Fact]
    public async Task A_dispatch_racing_a_crash_is_not_dispatched_and_doesnt_throw()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (connection, _, _) =>
        {
            connection.Exit(1);
            throw new ModHostClosedException("The mod host exited.");
        };

        var result = await Dispatch("turn.complete", S1).Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Restarting, "the crash was seen");

        // One that arrives while the host is restarting waits for it.
        Factory.OnDispatch = null;
        var waiting = Dispatch("turn.complete", S1);
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);
        (await waiting.Within()).Dispatched.ShouldBeTrue();
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_leaves_the_host_alone()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer();
        };
        using var cancel = new CancellationTokenSource();

        var dispatch = Dispatch("turn.complete", S1, ct: cancel.Token);
        await ModHostTests.Eventually(() => Factory.Current.Dispatches.Count == 1, "the dispatch was sent");
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => dispatch.Within());
        Factory.Current.Kills.ShouldBe(0);
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }

    // ── Strikes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failure_counts_and_is_logged_and_a_success_starts_the_count_over()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 1, "x is not a function")));

        var result = await Dispatch("turn.complete", S1).Within();

        result.Failures.ShouldHaveSingleItem().Mod.ShouldBe(Kept(Chips, 1));
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        Supervisor.StrikesOf(Kept(Demo, 1)).ShouldBe(0);
        var line = Rig.Log.Read(User, Kept(Chips, 1)).ShouldHaveSingleItem();
        (line.Level, line.Text).ShouldBe(("error", "turn.complete throw: x is not a function"));

        Factory.OnDispatch = null;
        await Dispatch("turn.complete", S1).Within();
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(0);
    }

    [Fact]
    public async Task Fleet_takes_the_hosts_count_when_it_is_higher()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 2)));

        await Dispatch("turn.complete", S1).Within();

        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(2);
    }

    [Fact]
    public async Task Fleets_count_lasts_across_a_restart()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 2)));
        await Dispatch("turn.complete", S1).Within();
        await CrashAsync();
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);

        // The new host counts from 1; Fleet's count is 3.
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 1)));
        await Dispatch("turn.complete", S1).Within();

        await ModHostTests.Eventually(() => Rig.Recorder.Calls.Count == 1, "the mod was turned off");
        Rig.Recorder.Calls.ShouldBe(["kept test-chips: boom"]);
    }

    [Fact]
    public async Task Three_strikes_turn_a_kept_mod_off_once_then_unload_it()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Rig.Recorder.OnRecord = (_, _, _) => hold.Task;
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 3, "too slow")));

        await Dispatch("turn.complete", S1).Within();
        await Dispatch("turn.complete", S1).Within();
        await ModHostTests.Eventually(() => Rig.Recorder.Calls.Count == 1, "the strike was recorded");
        await Task.Delay(50);

        Rig.Recorder.Calls.ShouldBe(["kept test-chips: too slow"]);
        hold.SetResult();
        await ModHostTests.Eventually(() => Factory.Current.Unloads.Contains(Kept(Chips, 1)), "the mod was unloaded");
        await ModHostTests.Eventually(() => Supervisor.StrikesOf(Kept(Chips, 1)) == 0, "the count started over");
        Supervisor.GetStatus().Loaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Three_strikes_turn_a_draft_off_in_its_session()
    {
        Rig.Draft(S1, Demo);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(DraftId(Demo, S1), 3)));

        await Dispatch("turn.complete", S1).Within();

        await ModHostTests.Eventually(() => Rig.Recorder.Calls.Count == 1, "the strike was recorded");
        Rig.Recorder.Calls.ShouldBe(["draft ses_test1/demo-mod: boom"]);
    }

    [Fact]
    public async Task A_recorder_that_reconciles_inline_doesnt_deadlock()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // What ModService does: write the store, raise mods.changed, and the service reconciles at once.
        Rig.Recorder.OnRecord = async (_, name, message) =>
        {
            await Rig.Store.SetOffAsync(User, name, new ModOff(ModOffBy.Strikes, Rig.Time.GetUtcNow(), message));
            await Supervisor.EnsureAsync().Within();
            recorded.SetResult();
        };
        Factory.OnDispatch = (_, _, _) => Task.FromResult(Answer(failures: Failure(Kept(Chips, 1), 3)));

        await Dispatch("turn.complete", S1).Within();

        await recorded.Task.Within();
        await ModHostTests.Eventually(() => !Supervisor.GetStatus().Loaded.Contains(Kept(Chips, 1)), "the struck mod left the host");
        Factory.Current.Unloads.ShouldContain(Kept(Chips, 1));
        Supervisor.GetStatus().Loaded.ShouldBe([Kept(Demo, 1)]);
        await Ensure();
    }

    [Fact]
    public async Task A_failed_notification_counts_like_a_failure_in_a_dispatch()
    {
        Rig.Keep(Chips);
        await StartedAsync();

        for (var i = 1; i <= 3; i++)
            Supervisor.HandleNotification("failed", ModHostTests.Json($$"""{ "mod": "test-chips@v1", "event": "ui.press", "kind": "throw", "message": "timer threw", "strikes": {{i}}, "sessionId": "ses_test1" }"""));

        await ModHostTests.Eventually(() => Rig.Recorder.Calls.Count == 1, "the strike was recorded");
        Rig.Recorder.Calls.ShouldBe(["kept test-chips: timer threw"]);
        var line = Rig.Log.Read(User, Kept(Chips, 1))[0];
        (line.Text, line.SessionId).ShouldBe(("ui.press throw: timer threw", S1));
    }

    // ── A host that stops answering ─────────────────────────────────────

    [Fact]
    public async Task A_hang_kills_the_host_strikes_the_only_mod_and_restarts()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => throw new TimeoutException();
        var hung = Factory.Current;

        var result = await Dispatch("turn.complete", S1).Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        hung.Kills.ShouldBe(1);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Restarting, "the host is restarting");
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.LastExit.ShouldBe(new ModHostExit(Rig.Time.GetUtcNow(), null, "didn't answer a dispatch within 15 s")),
            s => s.Restarts.ShouldBe(1));
        Rig.Log.Read(User, Kept(Chips, 1)).ShouldHaveSingleItem().Text
            .ShouldBe("The mod host didn't answer within 15 s while running turn.complete; Fleet restarted it. Suspects: test-chips@v1; struck: test-chips@v1.");

        Factory.OnDispatch = null;
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);
        Factory.Current.Loads.Select(l => l.Id).ShouldBe([Kept(Chips, 1)]);
    }

    [Fact]
    public async Task A_hang_strikes_the_mod_the_host_said_was_running()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        Factory.OnDispatch = (connection, d, _) =>
        {
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Demo, 1)}}", "event": "turn.complete", "sessionId": "ses_test1" }"""));
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Chips, 1)}}", "event": "turn.complete", "sessionId": "ses_test1" }"""));
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Demo, 1)}}", "event": "ui.render", "sessionId": "ses_test1" }"""));
            throw new TimeoutException();
        };

        await Dispatch("turn.complete", S1).Within();

        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        Supervisor.StrikesOf(Kept(Demo, 1)).ShouldBe(0);
        const string line = "The mod host didn't answer within 15 s while running turn.complete; Fleet restarted it. Suspects: demo-mod@v1, test-chips@v1; struck: test-chips@v1.";
        Rig.Log.Read(User, Kept(Chips, 1)).ShouldHaveSingleItem().Text.ShouldBe(line);
        Rig.Log.Read(User, Kept(Demo, 1)).ShouldHaveSingleItem().Text.ShouldBe(line);
    }

    [Fact]
    public async Task A_hang_with_no_running_notice_strikes_the_outermost_mod()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => throw new TimeoutException();

        await Dispatch("turn.complete", S1).Within();

        Supervisor.StrikesOf(Kept(Demo, 1)).ShouldBe(1);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(0);
    }

    [Fact]
    public async Task A_hang_in_the_session_start_the_host_runs_first_strikes_that_mod()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        // The host starts each mod in the session before the event, announcing those hooks as session.start.
        Factory.OnDispatch = (connection, _, _) =>
        {
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Demo, 1)}}", "event": "session.start", "sessionId": "ses_test1" }"""));
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Chips, 1)}}", "event": "session.start", "sessionId": "ses_test1" }"""));
            throw new TimeoutException();
        };

        await Dispatch("turn.complete", S1).Within();

        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        Supervisor.StrikesOf(Kept(Demo, 1)).ShouldBe(0);
    }

    [Fact]
    public async Task A_hang_in_a_control_callback_strikes_the_callbacks_owner()
    {
        Factory.Hooks[Chips] = [new("ui.render", null)];
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (connection, _, _) =>
        {
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Chips, 1)}}", "event": "ui.press", "sessionId": "ses_test1" }"""));
            throw new TimeoutException();
        };

        await Dispatch("ui.press", S1, """{ "handle": "h3" }""").Within();

        Factory.Current.Dispatches.ShouldHaveSingleItem().Mods.ShouldBeEmpty();
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        Rig.Log.Read(User, Kept(Chips, 1)).ShouldHaveSingleItem().Text
            .ShouldBe("The mod host didn't answer within 15 s while running ui.press; Fleet restarted it. Suspects: test-chips@v1; struck: test-chips@v1.");
    }

    [Fact]
    public async Task A_hang_in_a_control_event_nobody_was_named_for_strikes_nobody_but_still_restarts()
    {
        Factory.Hooks[Chips] = [new("ui.render", null)];
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => throw new TimeoutException();
        var hung = Factory.Current;

        (await Dispatch("ui.press", S1, """{ "handle": "h3" }""").Within()).ShouldBe(ModDispatchResult.NotDispatched);

        hung.Kills.ShouldBe(1);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(0);
        Rig.Log.Read(User, Kept(Chips, 1)).ShouldBeEmpty();
        await ModHostTests.Eventually(() => Supervisor.GetStatus().Restarts == 1, "the host restarts");
    }

    [Fact]
    public async Task A_running_notice_from_an_earlier_dispatch_doesnt_count()
    {
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();
        Factory.OnDispatch = (connection, _, _) =>
        {
            connection.Calls.HandleNotification("running", ModHostTests.Json($$"""{ "mod": "{{Kept(Chips, 1)}}", "event": "turn.complete", "sessionId": "ses_test1" }"""));
            return Task.FromResult(Answer());
        };
        await Dispatch("turn.complete", S1).Within();

        Factory.OnDispatch = (_, _, _) => throw new TimeoutException();
        await Dispatch("turn.complete", S1).Within();

        Supervisor.StrikesOf(Kept(Demo, 1)).ShouldBe(1);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_first_dispatch_to_time_out_on_a_host_restarts_it()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        Factory.OnDispatch = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref sent) == 2)
                both.SetResult();
            await both.Task;
            throw new TimeoutException();
        };
        var hung = Factory.Current;

        var results = await Task.WhenAll(Dispatch("turn.complete", S1), Dispatch("turn.complete", S2)).Within();

        results.ShouldAllBe(r => !r.Dispatched);
        hung.Kills.ShouldBe(1);
        Supervisor.StrikesOf(Kept(Chips, 1)).ShouldBe(1);
        await ModHostTests.Eventually(() => Supervisor.GetStatus().Restarts == 1, "one restart");
        await Task.Delay(50);
        Supervisor.GetStatus().Restarts.ShouldBe(1);
    }

    [Fact]
    public async Task Three_hangs_turn_the_mod_off()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        Factory.OnDispatch = (_, _, _) => throw new TimeoutException();

        for (var host = 1; host <= 3; host++)
        {
            await Dispatch("turn.complete", S1).Within();
            if (host < 3)
            {
                await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Restarting, "restarting");
                await RestartedAfterAsync(ModHostSupervisor.Backoff[host - 1], starts: host + 1);
            }
        }

        await ModHostTests.Eventually(() => Rig.Recorder.Calls.Count == 1, "the mod was turned off");
        Rig.Recorder.Calls[0].ShouldStartWith("kept test-chips: The mod host didn't answer within 15 s");
    }

    // ── Crashes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_crash_restarts_the_host_after_the_backoff_and_counts_it()
    {
        Rig.Keep(Chips);
        await StartedAsync();

        await CrashAsync(code: 3);

        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Restarting),
            s => s.LastExit.ShouldBe(new ModHostExit(Rig.Time.GetUtcNow(), 3, "exited with code 3")),
            s => s.Restarts.ShouldBe(1),
            s => s.Loaded.ShouldBeEmpty());
        await ModHostTests.Eventually(() => Rig.Signals.Signals.Contains("restarted test-user"), "Fleet heard of the restart");

        Rig.Time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Delay(50);
        Factory.Started.Count.ShouldBe(1);

        await RestartedAfterAsync(TimeSpan.FromMilliseconds(1), starts: 2);
        Factory.Current.Loads.Select(l => l.Id).ShouldBe([Kept(Chips, 1)]);
        Supervisor.GetStatus().Loaded.ShouldBe([Kept(Chips, 1)]);
    }

    [Fact]
    public async Task The_backoff_doubles_up_to_thirty_seconds_and_starts_over_after_a_minute_up()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        double[] expected = [0.5, 1, 2, 4, 8, 16, 30, 30];

        for (var i = 0; i < expected.Length; i++)
        {
            await CrashAsync();
            var delay = TimeSpan.FromSeconds(expected[i]);
            Rig.Time.Advance(delay - TimeSpan.FromMilliseconds(1));
            await Task.Delay(20);
            Factory.Started.Count.ShouldBe(i + 1, $"restart {i + 1} waits {delay}");
            await RestartedAfterAsync(TimeSpan.FromMilliseconds(1), starts: i + 2);
        }

        Rig.Time.Advance(TimeSpan.FromSeconds(60));
        await CrashAsync();
        Rig.Time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Delay(20);
        Factory.Started.Count.ShouldBe(expected.Length + 1);
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(1), starts: expected.Length + 2);
    }

    [Fact]
    public async Task A_restart_that_fails_is_not_ready_and_a_waiting_dispatch_gives_up()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        await CrashAsync();
        Factory.BeforeStart = _ => throw new ModHostNotReadyException("The mod host didn't start.");

        var dispatch = Dispatch("turn.complete", S1);
        Rig.Time.Advance(TimeSpan.FromMilliseconds(500));

        (await dispatch.Within()).ShouldBe(ModDispatchResult.NotDispatched);
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.NotReady, "not ready");
        Supervisor.GetStatus().Reason.ShouldBe("The mod host didn't start.");
    }

    [Fact]
    public async Task A_restart_starts_seen_sessions_again_with_reload_before_any_other_event()
    {
        Factory.Hooks[Chips] = [new("session.start", null), new("ui.render", null)];
        Rig.Keep(Chips);
        Rig.Session(S2);
        await StartedAsync();
        await Dispatch("ui.render", S1, """{ "component": "StatusChip" }""").Within();
        await CrashAsync();

        // A render that arrives while the host is down waits for it, and still goes after the reload start.
        var render = Dispatch("ui.render", S1, """{ "component": "StatusChip" }""");
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);
        (await render.Within()).Dispatched.ShouldBeTrue();
        await Dispatch("ui.render", S2, """{ "component": "StatusChip" }""").Within();

        var sent = Factory.Current.Dispatches;
        sent.Select(d => $"{d.Event} {d.SessionId}").ShouldBe(["session.start ses_test1", "ui.render ses_test1", "ui.render ses_test2"]);
        sent[0].ShouldSatisfyAllConditions(
            d => d.E.GetProperty("sessionId").GetString().ShouldBe(S1),
            d => d.E.GetProperty("reason").GetString().ShouldBe("reload"),
            d => d.Mods.ShouldBe([Kept(Chips, 1)]));
    }

    [Fact]
    public async Task A_render_right_after_a_restart_never_beats_the_reload_start()
    {
        Factory.Hooks[Chips] = [new("session.start", null), new("ui.render", null)];
        Rig.Keep(Chips);
        await StartedAsync();
        await Dispatch("ui.render", S1, """{ "component": "StatusChip" }""").Within();
        await Dispatch("ui.render", S2, """{ "component": "StatusChip" }""").Within();
        await CrashAsync();
        // The reload starts are slow to answer: renders for those sessions must wait for them.
        var starts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.OnDispatch = async (_, d, _) =>
        {
            if (d.Event == "session.start")
                await starts.Task;
            return Answer();
        };

        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);
        var renders = new[] { Dispatch("ui.render", S2, """{ "component": "StatusChip" }"""), Dispatch("ui.render", S1, """{ "component": "StatusChip" }""") };
        await Task.Delay(50);
        starts.SetResult();
        await Task.WhenAll(renders).Within();

        var order = Factory.Current.Dispatches.Select(d => $"{d.Event} {d.SessionId}").ToList();
        order.IndexOf("session.start ses_test1").ShouldBeLessThan(order.IndexOf("ui.render ses_test1"));
        order.IndexOf("session.start ses_test2").ShouldBeLessThan(order.IndexOf("ui.render ses_test2"));
        order.Count.ShouldBe(4);
    }

    [Fact]
    public async Task No_reload_start_is_sent_when_no_mod_hooks_session_start()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        await Dispatch("turn.complete", S1).Within();
        await CrashAsync();
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);

        await Dispatch("turn.complete", S1).Within();

        Factory.Current.Dispatches.Select(d => d.Event).ShouldBe(["turn.complete"]);
    }

    [Fact]
    public async Task Dispatches_and_calls_dont_wait_for_a_lifecycle_step()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.OnLoad = (_, load) => load.Name == Demo ? release.Task : Task.CompletedTask;
        Rig.Keep(Demo);
        var reconcile = Supervisor.EnsureAsync();
        await ModHostTests.Eventually(() => Factory.Current.Loads.Count == 2, "the slow load began");

        (await Dispatch("turn.complete", S1).Within()).Dispatched.ShouldBeTrue();
        await Factory.Current.Calls.HandleRequestAsync("store.keys", ModHostTests.Json("""{ "mod": "test-chips@v1", "sessionId": "ses_test1" }"""), CancellationToken.None).Within();
        reconcile.IsCompleted.ShouldBeFalse();

        release.SetResult();
        await reconcile.Within();
    }
}
