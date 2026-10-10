using System.Security.Cryptography;
using System.Text;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>When the host runs, what it loads, and how it follows the store, the switch and Bun.</summary>
public sealed class ModHostSupervisorLifecycleTests : ModHostSupervisorTestBase
{
    // ── When it starts ──────────────────────────────────────────────────

    [Fact]
    public async Task Never_starts_while_the_Mods_switch_is_off()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        Rig.Gate.SwitchedOn = false;

        await Ensure();

        Factory.Launches.ShouldBeEmpty();
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Stopped),
            s => s.Reason.ShouldBe("Mods are off."));
    }

    [Fact]
    public async Task Never_starts_when_started_without_mods()
    {
        Rig.Keep(Chips);
        Rig.Gate.SafeMode = true;

        await Ensure();

        Factory.Launches.ShouldBeEmpty();
        Supervisor.GetStatus().Reason.ShouldBe("Started without mods.");
    }

    [Fact]
    public async Task Never_starts_with_nothing_kept_and_on_or_drafted()
    {
        Rig.Keep(Chips, off: new ModOff(ModOffBy.User, Rig.Time.GetUtcNow()));
        Rig.Draft(S1, "off-draft", off: new ModOff(ModOffBy.Strikes, Rig.Time.GetUtcNow(), "boom"));
        Rig.Draft(S1, "half-written", withManifest: false);
        Rig.Session(S2, retention: "archived");
        Rig.Draft(S2, "archived-draft");
        Rig.Store.SeedDraft(User, "ses_gone", "orphan-draft");

        await Ensure();

        Factory.Launches.ShouldBeEmpty();
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Stopped),
            s => s.Reason.ShouldBe("No mod is kept or drafted."));
    }

    [Fact]
    public async Task Starts_and_loads_kept_mods_then_drafts_with_their_ids_roots_and_versions()
    {
        Rig.Keep(Chips, versions: 2);
        Rig.Keep(Demo);
        Rig.Draft(S1, Demo);

        await StartedAsync();

        var launch = Factory.Launches.ShouldHaveSingleItem();
        var userKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(User)))[..16];
        launch.ShouldBe(new ModHostLaunch("/bun/1.3.0/bun", "/fleet/mods-host/host.js", Rig.HostFolder, "0.49.0-test", userKey));

        var loads = Factory.Current.Loads;
        loads.Select(l => l.Id).ShouldBe([Kept(Demo, 1), Kept(Chips, 2), DraftId(Demo, S1)]);
        loads[0].ShouldSatisfyAllConditions(
            l => l.Name.ShouldBe(Demo),
            l => l.Version.GetInt32().ShouldBe(1),
            l => l.SessionId.ShouldBeNull(),
            l => l.Root.ShouldBe(Rig.Store.VersionFolder(User, Demo, 1)));
        loads[1].Root.ShouldBe(Rig.Store.VersionFolder(User, Chips, 2));
        loads[2].ShouldSatisfyAllConditions(
            l => l.Name.ShouldBe(Demo),
            l => l.Version.GetString().ShouldBe("draft"),
            l => l.SessionId.ShouldBe(S1));

        var status = Supervisor.GetStatus();
        status.Loaded.ShouldBe([DraftId(Demo, S1), Kept(Demo, 1), Kept(Chips, 2)]);
        status.ShouldSatisfyAllConditions(
            s => s.Reason.ShouldBeNull(),
            s => s.ProcessId.ShouldBe(Factory.Current.ProcessId),
            s => s.BunPath.ShouldBe("/bun/1.3.0/bun"),
            s => s.BunVersion.ShouldBe("1.3.0"),
            s => s.HostVersion.ShouldBe("0.1.0-test"),
            s => s.StartedAt.ShouldBe(Rig.Time.GetUtcNow()),
            s => s.Restarts.ShouldBe(0),
            s => s.LastExit.ShouldBeNull());
    }

    [Fact]
    public async Task A_draft_loads_from_a_staged_copy_whose_folder_is_named_after_the_mod()
    {
        Rig.Draft(S1, Demo);

        await StartedAsync();

        var root = Factory.Current.Loads.ShouldHaveSingleItem().Root;
        Path.GetFileName(root).ShouldBe(Demo);
        Path.GetDirectoryName(Path.GetDirectoryName(root)).ShouldBe(Path.Combine(Rig.StagedDrafts, S1));
        Directory.Exists(root).ShouldBeTrue();
        Rig.Store.StagedDrafts.ShouldHaveSingleItem().ShouldBe((S1, Demo, root));
        root.ShouldNotBe(Rig.Store.DraftFolder(User, S1, Demo));
    }

    [Fact]
    public async Task Start_clears_staged_copies_left_from_before()
    {
        var leftover = Path.Combine(Rig.StagedDrafts, "ses_old", "7", "old-mod");
        Directory.CreateDirectory(leftover);
        Rig.Keep(Chips);

        await StartedAsync();

        Directory.Exists(leftover).ShouldBeFalse();
    }

    [Fact]
    public async Task Starts_watching_the_drafts_once()
    {
        Rig.Keep(Chips);

        await StartedAsync();
        await Ensure();

        Rig.Watcher.Roots.ShouldBe([Rig.Store.DraftsRoot(User)]);
    }

    // ── Stopping ────────────────────────────────────────────────────────

    [Fact]
    public async Task Stops_when_the_last_mod_goes()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var host = Factory.Current;

        await Rig.Store.SetOffAsync(User, Chips, new ModOff(ModOffBy.User, Rig.Time.GetUtcNow()));
        Rig.Store.RemoveDraft(User, S1, Demo);
        await Ensure();

        host.ShutdownGrace.ShouldBe(TimeSpan.FromSeconds(2));
        host.Disposed.ShouldBeTrue();
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Stopped),
            s => s.Reason.ShouldBe("No mod is kept or drafted."),
            s => s.Loaded.ShouldBeEmpty(),
            s => s.Restarts.ShouldBe(0),
            s => s.LastExit.ShouldBeNull());
        Directory.Exists(Rig.StagedDrafts).ShouldBeFalse();
        Rig.Watcher.Open.ShouldBe(0);
        // A stop Fleet asked for isn't a crash: nothing restarts.
        Rig.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Factory.Started.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, "Mods are off.")]
    [InlineData(true, "Started without mods.")]
    public async Task Stops_when_the_switch_turns_off_or_mods_are_stopped(bool safeMode, string reason)
    {
        Rig.Keep(Chips);
        await StartedAsync();

        if (safeMode)
            Rig.Gate.SafeMode = true;
        else
            Rig.Gate.SwitchedOn = false;
        await Ensure();

        Factory.Log.ShouldContain("shutdown");
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Stopped),
            s => s.Reason.ShouldBe(reason));
    }

    // ── Following the store ─────────────────────────────────────────────

    [Fact]
    public async Task A_change_loads_and_unloads_only_the_difference()
    {
        Rig.Keep(Chips);
        Rig.Keep("other-mod");
        await StartedAsync();

        await Rig.Store.SetOffAsync(User, Chips, new ModOff(ModOffBy.User, Rig.Time.GetUtcNow()));
        Rig.Keep(Demo);
        await Ensure();

        Factory.Started.Count.ShouldBe(1);
        Factory.Current.Unloads.ShouldBe([Kept(Chips, 1)]);
        Factory.Current.Loads.Select(l => l.Id).ShouldBe([Kept("other-mod", 1), Kept(Chips, 1), Kept(Demo, 1)]);
        Supervisor.GetStatus().Loaded.ShouldBe([Kept(Demo, 1), Kept("other-mod", 1)]);
    }

    [Fact]
    public async Task Undo_unloads_the_newer_version_then_loads_the_older()
    {
        Rig.Keep(Chips, versions: 2);
        await StartedAsync();

        await Rig.Store.UndoAsync(User, Chips, Rig.Time.GetUtcNow());
        await Ensure();

        Factory.Log.SkipWhile(e => e != $"load {Kept(Chips, 2)}").Skip(1).ShouldBe([$"unload {Kept(Chips, 2)}", $"load {Kept(Chips, 1)}"]);
        Factory.Current.Loads[^1].Root.ShouldBe(Rig.Store.VersionFolder(User, Chips, 1));
        Supervisor.GetStatus().Loaded.ShouldBe([Kept(Chips, 1)]);
    }

    [Fact]
    public async Task A_draft_of_a_kept_mod_loads_beside_it()
    {
        Rig.Keep(Chips);
        await StartedAsync();

        Rig.Draft(S1, Chips);
        await Ensure();

        Factory.Current.Unloads.ShouldBeEmpty();
        Supervisor.GetStatus().Loaded.ShouldBe([DraftId(Chips, S1), Kept(Chips, 1)]);
    }

    [Fact]
    public async Task A_draft_whose_session_is_archived_is_unloaded_and_its_copy_deleted()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var copy = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();

        Rig.Session(S1, retention: "archived");
        await Ensure();

        Factory.Current.Unloads.ShouldBe([DraftId(Demo, S1)]);
        Directory.Exists(copy).ShouldBeFalse();
    }

    // ── Refused loads ───────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_load_keeps_the_problem_and_a_log_line()
    {
        var report = ModHostTests.Json("""{ "ok": false, "errors": [{ "code": "import", "message": "no imports" }] }""");
        Factory.Refusals[Kept(Chips, 1)] = ("test-chips doesn't load: no imports", report);
        Rig.Keep(Chips);

        await StartedAsync();

        var problem = Supervisor.GetLoadProblem(Kept(Chips, 1)).ShouldNotBeNull();
        problem.Message.ShouldBe("test-chips doesn't load: no imports");
        problem.Report.ShouldNotBeNull().GetProperty("ok").GetBoolean().ShouldBeFalse();
        problem.At.ShouldBe(Rig.Time.GetUtcNow());
        var line = Rig.Log.Read(User, Kept(Chips, 1)).ShouldHaveSingleItem();
        (line.Level, line.Text, line.SessionId).ShouldBe(("error", "test-chips doesn't load: no imports", null));
        Supervisor.GetStatus().Loaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_kept_mod_is_not_tried_again_until_the_store_changes()
    {
        Factory.Refusals[Kept(Chips, 1)] = ("no", null);
        Rig.Keep(Chips);
        await StartedAsync();

        await Ensure();
        Factory.Current.Loads.Count.ShouldBe(1);

        Factory.Refusals.Clear();
        Supervisor.ForgetRefusals();
        await Ensure();

        Factory.Current.Loads.Count.ShouldBe(2);
        Supervisor.GetStatus().Loaded.ShouldBe([Kept(Chips, 1)]);
        Supervisor.GetLoadProblem(Kept(Chips, 1)).ShouldBeNull();
    }

    [Fact]
    public async Task A_problem_goes_when_its_mod_is_no_longer_wanted()
    {
        Factory.Refusals[Kept(Chips, 1)] = ("no", null);
        Rig.Keep(Chips);
        Rig.Keep(Demo);
        await StartedAsync();

        await Rig.Store.SetOffAsync(User, Chips, new ModOff(ModOffBy.User, Rig.Time.GetUtcNow()));
        await Ensure();

        Supervisor.GetLoadProblem(Kept(Chips, 1)).ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_draft_is_tried_again_on_its_next_save()
    {
        Factory.Refusals[DraftId(Demo, S1)] = ("demo-mod doesn't load: register threw", null);
        Rig.Draft(S1, Demo);
        await StartedAsync();
        await Ensure();
        Factory.Current.Loads.Count.ShouldBe(1);
        Rig.StagedCopies(S1, Demo).ShouldBeEmpty();
        var line = Rig.Log.Read(User, DraftId(Demo, S1)).ShouldHaveSingleItem();
        line.SessionId.ShouldBe(S1);

        Factory.Refusals.Clear();
        Rig.Watcher.Save(S1, Demo);

        await ModHostTests.Eventually(() => Supervisor.GetStatus().Loaded.Contains(DraftId(Demo, S1)), "the saved draft loaded");
        Supervisor.GetLoadProblem(DraftId(Demo, S1)).ShouldBeNull();
    }

    [Fact]
    public async Task A_draft_the_store_cant_stage_is_a_problem_without_a_report()
    {
        Rig.Store.StageRefusal = "demo-mod has a link in it.";
        Rig.Draft(S1, Demo);

        await StartedAsync();

        Factory.Current.Loads.ShouldBeEmpty();
        var problem = Supervisor.GetLoadProblem(DraftId(Demo, S1)).ShouldNotBeNull();
        (problem.Message, problem.Report).ShouldBe(("demo-mod has a link in it.", null));
    }

    // ── Saving a draft ──────────────────────────────────────────────────

    [Fact]
    public async Task Saving_a_draft_stages_it_again_reloads_it_and_then_deletes_the_old_copy()
    {
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var first = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();

        string? oldCopyDuringLoad = null;
        Factory.OnLoad = (_, load) =>
        {
            oldCopyDuringLoad = Directory.Exists(first) ? "there" : "gone";
            return Task.CompletedTask;
        };
        Rig.Watcher.Save(S1, Demo);

        await ModHostTests.Eventually(() => Factory.Current.Loads.Count == 2, "the draft reloaded");
        await ModHostTests.Eventually(() => !Directory.Exists(first), "the old copy was deleted");
        oldCopyDuringLoad.ShouldBe("there");
        var second = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();
        second.ShouldNotBe(first);
        Factory.Current.Loads[^1].ShouldSatisfyAllConditions(
            l => l.Id.ShouldBe(DraftId(Demo, S1)),
            l => l.Root.ShouldBe(second));
        Factory.Current.Unloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_reload_keeps_the_old_draft_loaded_and_its_copy()
    {
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var first = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();

        Factory.Refusals[DraftId(Demo, S1)] = ("demo-mod doesn't load: syntax", null);
        Rig.Watcher.Save(S1, Demo);

        await ModHostTests.Eventually(() => Supervisor.GetLoadProblem(DraftId(Demo, S1)) is not null, "the reload was refused");
        Rig.StagedCopies(S1, Demo).ShouldBe([first]);
        Supervisor.GetStatus().Loaded.ShouldBe([DraftId(Demo, S1)]);
    }

    [Fact]
    public async Task A_draft_saved_without_a_valid_manifest_is_unloaded()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var copy = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();

        Rig.Store.SeedDraft(User, S1, Demo, withManifest: false);
        Rig.Watcher.Save(S1, Demo);

        await ModHostTests.Eventually(() => Factory.Current.Unloads.Contains(DraftId(Demo, S1)), "the draft was unloaded");
        await ModHostTests.Eventually(() => !Directory.Exists(copy), "its copy was deleted");
    }

    [Fact]
    public async Task A_new_draft_folder_is_picked_up_when_it_is_saved()
    {
        Rig.Keep(Chips);
        await StartedAsync();

        Rig.Draft(S1, Demo);
        Rig.Watcher.Save(S1, Demo);

        await ModHostTests.Eventually(() => Supervisor.GetStatus().Loaded.Contains(DraftId(Demo, S1)), "the new draft loaded");
    }

    // ── Staged copies ───────────────────────────────────────────────────

    [Fact]
    public async Task Forgetting_a_session_deletes_its_staged_copies_once_nothing_of_it_is_loaded()
    {
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        await StartedAsync();
        var copy = Rig.StagedCopies(S1, Demo).ShouldHaveSingleItem();

        // Still loaded: the copy stays, the unload cleans it.
        await Supervisor.ForgetSessionAsync(S1).Within();
        Factory.Current.Forgotten.ShouldBe([S1]);
        Directory.Exists(copy).ShouldBeTrue();

        Rig.Store.RemoveDraft(User, S1, Demo);
        await Ensure();
        Directory.Exists(copy).ShouldBeFalse();

        // A copy left from a refused or crashed load goes with the session.
        var stray = Path.Combine(Rig.StagedDrafts, S1, "99", Demo);
        Directory.CreateDirectory(stray);
        await Supervisor.ForgetSessionAsync(S1).Within();
        Directory.Exists(Path.Combine(Rig.StagedDrafts, S1)).ShouldBeFalse();
    }

    // ── Bun ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_Bun_restarts_the_host_on_it_then_prunes_the_old_one()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        var old = Factory.Current;

        Rig.Bun.Location = new BunLocation("/bun/1.4.0/bun", "fleet", "1.4.0");
        await Ensure();

        old.ShutdownGrace.ShouldBe(TimeSpan.FromSeconds(2));
        Factory.Started.Count.ShouldBe(2);
        Factory.Current.Launch.BunPath.ShouldBe("/bun/1.4.0/bun");
        Factory.Current.Loads.Select(l => l.Id).ShouldBe([Kept(Chips, 1)]);
        Rig.Bun.Pruned.ShouldHaveSingleItem().ShouldBe(["/bun/1.4.0/bun"]);
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.Running),
            s => s.BunPath.ShouldBe("/bun/1.4.0/bun"),
            s => s.Restarts.ShouldBe(0),
            s => s.LastExit.ShouldBeNull());
    }

    [Fact]
    public async Task Bun_gone_while_the_host_runs_keeps_it_running()
    {
        Rig.Keep(Chips);
        await StartedAsync();

        Rig.Bun.Location = null;
        await Ensure();

        Factory.Log.ShouldNotContain("shutdown");
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }

    [Fact]
    public async Task No_Bun_is_not_ready_until_one_is_found()
    {
        Rig.Keep(Chips);
        Rig.Bun.Location = null;

        await Ensure();

        Factory.Launches.ShouldBeEmpty();
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.NotReady),
            s => s.Reason.ShouldBe("The mod runtime (Bun) isn't installed yet."));

        Rig.Bun.Location = new BunLocation("/bun/1.3.0/bun", "fleet", "1.3.0");
        await StartedAsync();
    }

    [Fact]
    public async Task No_host_script_is_not_ready()
    {
        Rig.Keep(Chips);
        Rig.Files.HostScript = null;

        await Ensure();

        Factory.Launches.ShouldBeEmpty();
        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.NotReady),
            s => s.Reason.ShouldBe("Fleet can't find the mod host (mods-host/host.js)."));
    }

    [Fact]
    public async Task A_host_that_wont_start_is_not_ready_with_the_reason_and_a_dispatch_doesnt_start_it()
    {
        Rig.Keep(Chips);
        Factory.BeforeStart = _ => throw new ModHostNotReadyException("The mod host answered with protocol 2.");

        await Ensure();

        Supervisor.GetStatus().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ModHostStates.NotReady),
            s => s.Reason.ShouldBe("The mod host answered with protocol 2."));

        Factory.BeforeStart = null;
        (await Dispatch("turn.complete", S1).Within()).ShouldBe(ModDispatchResult.NotDispatched);
        Factory.Launches.Count.ShouldBe(1);
    }

    // ── Checks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_check_starts_the_host_for_a_minute_then_it_stops()
    {
        var folder = Path.Combine(Rig.Root, "check", Demo);

        var report = await Supervisor.CheckAsync(folder).Within();

        report.GetProperty("ok").GetBoolean().ShouldBeTrue();
        Factory.Current.Checks.ShouldBe([(folder, Path.Combine(folder, "mod.json"))]);
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);

        Rig.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);

        Rig.Time.Advance(TimeSpan.FromSeconds(1));
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Stopped, "the lease ended and the host stopped");
        Factory.Current.ShutdownGrace.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_second_check_extends_the_lease()
    {
        await Supervisor.CheckAsync(Path.Combine(Rig.Root, "a")).Within();
        Rig.Time.Advance(TimeSpan.FromSeconds(40));
        await Supervisor.CheckAsync(Path.Combine(Rig.Root, "b")).Within();

        Rig.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
        Factory.Started.Count.ShouldBe(1);

        Rig.Time.Advance(TimeSpan.FromSeconds(30));
        await ModHostTests.Eventually(() => Supervisor.GetStatus().State == ModHostStates.Stopped, "the later lease ended");
    }

    [Fact]
    public async Task A_check_with_the_switch_off_is_not_ready()
    {
        Rig.Gate.SwitchedOn = false;

        var thrown = await Should.ThrowAsync<ModHostNotReadyException>(() => Supervisor.CheckAsync(Rig.Root).Within());

        thrown.Message.ShouldBe(ModsFeature.TurnedOffMessage);
        Factory.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_check_without_Bun_is_not_ready_with_the_reason()
    {
        Rig.Bun.Location = null;

        var thrown = await Should.ThrowAsync<ModHostNotReadyException>(() => Supervisor.CheckAsync(Rig.Root).Within());

        thrown.Message.ShouldBe("The mod runtime (Bun) isn't installed yet.");
    }

    [Fact]
    public async Task A_host_started_for_a_check_in_safe_mode_loads_and_runs_no_mod()
    {
        Factory.Hooks[Chips] = [new("ui.press", null), new("session.start", null), new("ui.render", null)];
        Rig.Keep(Chips);
        Rig.Draft(S1, Demo);
        Rig.Gate.SafeMode = true;

        await Supervisor.CheckAsync(Path.Combine(Rig.Root, "check")).Within();
        await Ensure();
        (await Dispatch("ui.render", S1, """{ "component": "StatusChip" }""").Within()).Dispatched.ShouldBeFalse();
        (await Dispatch("ui.press", S1, """{ "handle": "h1" }""", "desktop").Within()).Dispatched.ShouldBeFalse();
        await CrashAsync();
        await RestartedAfterAsync(TimeSpan.FromMilliseconds(500), starts: 2);

        Factory.Started.SelectMany(c => c.Loads).ShouldBeEmpty();
        Factory.Started.SelectMany(c => c.Dispatches).ShouldBeEmpty();
        Supervisor.GetStatus().Loaded.ShouldBeEmpty();
    }

    // ── Shutdown and the gate ───────────────────────────────────────────

    [Fact]
    public async Task Shutdown_stops_the_host_and_nothing_starts_it_again()
    {
        Rig.Keep(Chips);
        await StartedAsync();
        var host = Factory.Current;

        await Supervisor.ShutdownAsync().Within();
        await Ensure();
        Rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        host.ShutdownGrace.ShouldBe(TimeSpan.FromSeconds(2));
        host.Disposed.ShouldBeTrue();
        Factory.Started.Count.ShouldBe(1);
        Rig.Watcher.Open.ShouldBe(0);
        Directory.Exists(Rig.StagedDrafts).ShouldBeFalse();
    }

    [Fact]
    public async Task One_lifecycle_step_at_a_time()
    {
        Rig.Keep(Chips);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.BeforeStart = _ => release.Task;

        var first = Supervisor.EnsureAsync();
        var second = Supervisor.EnsureAsync();
        await ModHostTests.Eventually(() => Factory.Launches.Count == 1, "the first start began");
        await Task.Delay(50);
        second.IsCompleted.ShouldBeFalse();
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Starting);

        release.SetResult();
        await Task.WhenAll(first, second).Within();

        Factory.Launches.Count.ShouldBe(1);
        Supervisor.GetStatus().State.ShouldBe(ModHostStates.Running);
    }
}
