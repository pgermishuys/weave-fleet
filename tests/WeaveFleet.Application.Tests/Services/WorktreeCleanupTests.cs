using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Clean now against real git: which checkouts go, which stay and why, that only the folder goes, and that it comes
/// back at the same path on the same branch.
/// </summary>
public sealed class WorktreeCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly RealGitRepository _repository = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InstanceTracker _instances = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly OpenTerminals _terminals = new();
    private readonly WorkspaceService _workspaceService;
    private readonly WorktreeCleanup _cleanup;

    public WorktreeCleanupTests()
    {
        _workspaceService = new WorkspaceService(_workspaces, new TestUserContext(), new FleetOptions(), NullLogger<WorkspaceService>.Instance);
        _cleanup = new WorktreeCleanup(
            _workspaceService, _workspaces, _sessions, _instances, _activity, _broadcaster,
            NullLogger<WorktreeCleanup>.Instance, _terminals, new FakeTimeProvider(Now));
    }

    public void Dispose() => _repository.Dispose();

    [Fact]
    public async Task a_checkout_archived_for_a_week_goes_and_comes_back_at_the_same_path_with_its_commits()
    {
        var workspace = await WorktreeAsync("fleet/export-csv");
        var session = Archived(workspace, daysAgo: 9, title: "Export to CSV");
        Commit(workspace, "export.csv", "id,total\n");

        var preview = (await _cleanup.ListAsync()).ShouldHaveSingleItem();
        preview.Verdict.ShouldBe(CheckoutVerdicts.Remove);
        preview.Reason.ShouldBe("Archived 9 days ago.");
        preview.Repository.ShouldBe("repo");
        preview.Title.ShouldBe("Export to CSV");

        var report = await _cleanup.CleanAsync([workspace.Id]);

        report.Removed.ShouldHaveSingleItem().SessionId.ShouldBe(session.Id);
        Directory.Exists(workspace.Directory).ShouldBeFalse();
        _repository.Git("branch", "--list", "fleet/export-csv").ShouldContain("fleet/export-csv");
        workspace.CleanedUpAt.ShouldNotBeNull();
        var removed = _broadcaster.Broadcasts.ShouldHaveSingleItem();
        (removed.Topic, removed.Type).ShouldBe(("sessions", WorktreeCleanup.CheckoutEventType));
        removed.Payload.GetProperty("workspaceId").GetString().ShouldBe(workspace.Id);
        removed.Payload.GetProperty("state").GetString().ShouldBe(CheckoutStates.Removed);

        var restored = await _cleanup.RestoreAsync(workspace.Id, "test-user");

        restored.IsSuccess.ShouldBeTrue();
        File.ReadAllText(Path.Combine(workspace.Directory, "export.csv")).ShouldBe("id,total\n");
        _repository.Git("-C", workspace.Directory, "branch", "--show-current").Trim().ShouldBe("fleet/export-csv");
        workspace.CleanedUpAt.ShouldBeNull();
        _broadcaster.Broadcasts.Select(b => b.Payload.GetProperty("state").GetString())
            .ShouldBe([CheckoutStates.Removed, CheckoutStates.Restoring, CheckoutStates.Restored]);
    }

    [Fact]
    public async Task only_sessions_archived_for_a_week_are_candidates()
    {
        var recent = await WorktreeAsync("fleet/recent");
        Archived(recent, daysAgo: 2);
        var active = await WorktreeAsync("fleet/active");
        SessionIn(active, "active");

        var checkouts = await _cleanup.ListAsync();

        Verdict(checkouts, recent).ShouldBe((CheckoutVerdicts.Recent, "Archived 2 days ago."));
        Verdict(checkouts, active).ShouldBe((CheckoutVerdicts.InUse, "The session isn't archived."));

        var report = await _cleanup.CleanAsync([recent.Id, active.Id]);
        report.Removed.ShouldBeEmpty();
        Directory.Exists(recent.Directory).ShouldBeTrue();
        Directory.Exists(active.Directory).ShouldBeTrue();
    }

    [Fact]
    public async Task a_checkout_whose_status_git_cannot_read_is_kept_not_treated_as_clean()
    {
        var workspace = await WorktreeAsync("fleet/unreadable");
        Archived(workspace, daysAgo: 10);
        File.WriteAllText(Path.Combine(workspace.Directory, "work.md"), "unsaved thoughts");
        var gitDir = _repository.Git("rev-parse", "--git-path", "worktrees").Trim();
        var index = Directory.GetDirectories(Path.Combine(_repository.Path, gitDir)).Single();
        File.WriteAllText(Path.Combine(index, "index"), "not an index");

        var report = await _cleanup.CleanAsync([workspace.Id]);

        report.Removed.ShouldBeEmpty();
        report.Kept.ShouldHaveSingleItem().Reason.ShouldStartWith("Fleet couldn't read it");
        File.Exists(Path.Combine(workspace.Directory, "work.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task uncommitted_work_and_stray_ignored_files_keep_a_checkout_but_rebuilt_output_does_not()
    {
        var edited = await WorktreeAsync("fleet/edited");
        Archived(edited, daysAgo: 10);
        File.WriteAllText(Path.Combine(edited.Directory, "notes.md"), "draft");
        File.WriteAllText(Path.Combine(edited.Directory, "todo.md"), "draft");

        var secrets = await WorktreeAsync("fleet/secrets");
        Archived(secrets, daysAgo: 10);
        Commit(secrets, ".gitignore", ".env.local\nnode_modules/\n");
        File.WriteAllText(Path.Combine(secrets.Directory, ".env.local"), "API_KEY=invented");

        var dependencies = await WorktreeAsync("fleet/dependencies");
        Archived(dependencies, daysAgo: 10);
        Commit(dependencies, ".gitignore", "node_modules/\n");
        Directory.CreateDirectory(Path.Combine(dependencies.Directory, "web", "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(dependencies.Directory, "web", "node_modules", "left-pad", "index.js"), "");

        var checkouts = await _cleanup.ListAsync();

        Verdict(checkouts, edited).ShouldBe((CheckoutVerdicts.Keep, "Has uncommitted changes (2 files)."));
        Verdict(checkouts, secrets).ShouldBe((CheckoutVerdicts.Keep, "Has an ignored file you may want: .env.local"));
        Verdict(checkouts, dependencies).Verdict.ShouldBe(CheckoutVerdicts.Remove);
    }

    [Fact]
    public async Task a_folder_another_session_uses_is_kept_but_its_own_subagents_do_not_count()
    {
        var shared = await WorktreeAsync("fleet/shared");
        Archived(shared, daysAgo: 8);
        SessionIn(shared, "archived", archivedDaysAgo: 8);

        var forked = await WorktreeAsync("fleet/forked");
        Archived(forked, daysAgo: 8);
        var fork = new Workspace { Id = "ws-fork", Directory = forked.Directory, IsolationStrategy = "existing", CreatedAt = "2026-10-01", UserId = "test-user" };
        _workspaces.Seed(fork);
        SessionIn(fork, "active");

        var delegated = await WorktreeAsync("fleet/delegated");
        var parent = Archived(delegated, daysAgo: 8);
        SessionIn(delegated, "archived", archivedDaysAgo: 8).ParentSessionId = parent.Id;

        var checkouts = await _cleanup.ListAsync();

        Verdict(checkouts, shared).ShouldBe((CheckoutVerdicts.Keep, "Another session uses this folder."));
        Verdict(checkouts, forked).ShouldBe((CheckoutVerdicts.Keep, "Another session uses this folder."));
        Verdict(checkouts, delegated).Verdict.ShouldBe(CheckoutVerdicts.Remove);
    }

    [Fact]
    public async Task a_working_agent_or_subagent_or_an_open_terminal_keeps_a_checkout()
    {
        var working = await WorktreeAsync("fleet/working");
        var session = Archived(working, daysAgo: 8);
        _activity.Update(session.Id, "busy", "test-user");

        var delegating = await WorktreeAsync("fleet/delegating");
        var parent = Archived(delegating, daysAgo: 8);
        var child = SessionIn(delegating, "archived", archivedDaysAgo: 8);
        child.ParentSessionId = parent.Id;
        _activity.Update(child.Id, "busy", "test-user");

        var terminal = await WorktreeAsync("fleet/terminal");
        _terminals.Open.Add(Archived(terminal, daysAgo: 8).Id);

        var checkouts = await _cleanup.ListAsync();

        Verdict(checkouts, working).ShouldBe((CheckoutVerdicts.Keep, "The agent is working."));
        Verdict(checkouts, delegating).Verdict.ShouldBe(CheckoutVerdicts.Keep);
        Verdict(checkouts, terminal).ShouldBe((CheckoutVerdicts.Keep, "A terminal is open in it."));
    }

    [Fact]
    public async Task a_checkout_off_any_branch_is_kept_because_it_could_not_come_back()
    {
        var detached = await WorktreeAsync("fleet/detached");
        Archived(detached, daysAgo: 8);
        _repository.Git("-C", detached.Directory, "checkout", "--detach");

        Verdict(await _cleanup.ListAsync(), detached)
            .ShouldBe((CheckoutVerdicts.Keep, "It isn't on a branch, so it couldn't come back."));
    }

    [Fact]
    public async Task unpushed_commits_do_not_block_removal_and_the_report_counts_them()
    {
        _repository.AddOrigin();
        var workspace = await WorktreeAsync("fleet/unpushed");
        Archived(workspace, daysAgo: 8);
        Commit(workspace, "a.txt", "a");
        Commit(workspace, "b.txt", "b");

        var removed = (await _cleanup.CleanAsync([workspace.Id])).Removed.ShouldHaveSingleItem();

        removed.UnpushedCommits.ShouldBe(2);
        removed.Reason.ShouldBe("Archived 8 days ago. 2 commits only on this machine, kept on fleet/unpushed.");
        _repository.Git("log", "--format=%s", "fleet/unpushed", "-2").ShouldContain("add b.txt");
    }

    [Fact]
    public async Task clean_checks_again_so_work_since_the_preview_keeps_the_checkout()
    {
        var workspace = await WorktreeAsync("fleet/late-edit");
        Archived(workspace, daysAgo: 8);
        (await _cleanup.ListAsync()).ShouldHaveSingleItem().Verdict.ShouldBe(CheckoutVerdicts.Remove);
        File.WriteAllText(Path.Combine(workspace.Directory, "late.md"), "written after the preview");

        var report = await _cleanup.CleanAsync([workspace.Id]);

        report.Kept.ShouldHaveSingleItem().Reason.ShouldBe("Has uncommitted changes (1 file).");
        File.Exists(Path.Combine(workspace.Directory, "late.md")).ShouldBeTrue();
        workspace.CleanedUpAt.ShouldBeNull();
    }

    [Fact]
    public async Task the_harness_lets_go_of_the_folder_before_it_goes()
    {
        var workspace = await WorktreeAsync("fleet/live");
        var session = Archived(workspace, daysAgo: 8);
        var instance = new FakeHarnessSession(session.InstanceId);
        _instances.Register(session.InstanceId, instance);

        await _cleanup.CleanAsync([workspace.Id]);

        instance.StopCalled.ShouldBeTrue();
        instance.DeleteCalled.ShouldBeFalse();
        _instances.Get(session.InstanceId).ShouldBeNull();
    }

    [Fact]
    public async Task a_branch_deleted_since_cannot_come_back_and_says_so()
    {
        var workspace = await WorktreeAsync("fleet/gone");
        Archived(workspace, daysAgo: 8);
        await _cleanup.CleanAsync([workspace.Id]);
        _repository.Git("branch", "-D", "fleet/gone");

        var restored = await _cleanup.RestoreAsync(workspace.Id, "test-user");

        restored.IsFailure.ShouldBeTrue();
        restored.Error.Description.ShouldBe("The branch fleet/gone isn't there any more. The conversation is kept.");
        workspace.CleanedUpAt.ShouldNotBeNull();
        _broadcaster.Broadcasts[^1].Payload.GetProperty("state").GetString().ShouldBe(CheckoutStates.Removed);
    }

    [Fact]
    public async Task a_folder_something_else_took_is_left_alone()
    {
        var workspace = await WorktreeAsync("fleet/taken");
        Archived(workspace, daysAgo: 8);
        await _cleanup.CleanAsync([workspace.Id]);
        Directory.CreateDirectory(workspace.Directory);
        File.WriteAllText(Path.Combine(workspace.Directory, "mine.txt"), "someone else's");

        var restored = await _cleanup.RestoreAsync(workspace.Id, "test-user");

        restored.IsFailure.ShouldBeTrue();
        restored.Error.Description.ShouldStartWith("Something else is in ");
        File.ReadAllText(Path.Combine(workspace.Directory, "mine.txt")).ShouldBe("someone else's");
    }

    [Fact]
    public async Task sizes_count_everything_in_the_folder_up_to_the_cap()
    {
        var workspace = await WorktreeAsync("fleet/sized");
        File.WriteAllBytes(Path.Combine(workspace.Directory, "blob.bin"), new byte[4096]);

        var size = await _cleanup.MeasureAsync(workspace.Id);
        var capped = await WorktreeCleanup.MeasureFolderAsync(workspace.Directory, maxEntries: 1);

        size.ShouldNotBeNull();
        size.Complete.ShouldBeTrue();
        size.Bytes.ShouldBeGreaterThanOrEqualTo(4096);
        capped.Complete.ShouldBeFalse();
    }

    [Theory]
    [InlineData(" M src/app.ts\0?? notes.md\0", 2, null)]
    [InlineData("R  new.ts\0old.ts\0", 1, null)]
    [InlineData("!! node_modules/\0!! web/dist/\0!! .env\0", 0, ".env")]
    [InlineData("!! packages/api/bin/\0!! packages/api/obj/\0", 0, null)]
    public void status_counts_changes_and_finds_ignored_files_worth_keeping(string porcelain, int changed, string? stray)
        => WorktreeCleanup.ReadStatus(porcelain).ShouldBe((changed, stray));

    private async Task<Workspace> WorktreeAsync(string branch)
    {
        var created = await _workspaceService.CreateWorkspaceAsync(_repository.Path, "worktree", branch);
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value;
    }

    private Session Archived(Workspace workspace, int daysAgo, string title = "Invented task")
        => SessionIn(workspace, "archived", daysAgo, title);

    private Session SessionIn(Workspace workspace, string retention, int archivedDaysAgo = 0, string title = "Invented task")
    {
        var id = $"sess-{Guid.NewGuid():N}";
        var session = new Session
        {
            Id = id,
            WorkspaceId = workspace.Id,
            InstanceId = $"inst-{id}",
            Title = title,
            Directory = workspace.Directory,
            CreatedAt = $"2026-10-01T00:00:{_sessions.All.Count:00}Z",
            RetentionStatus = retention,
            ArchivedAt = retention == "archived" ? Now.AddDays(-archivedDaysAgo).AddHours(-1).ToString("O") : null,
            UserId = "test-user",
        };
        _sessions.Seed(session);
        return session;
    }

    private void Commit(Workspace workspace, string file, string content)
    {
        File.WriteAllText(Path.Combine(workspace.Directory, file), content);
        _repository.Git("-C", workspace.Directory, "add", file);
        _repository.Git("-C", workspace.Directory, "commit", "-m", $"add {file}");
    }

    private static (string Verdict, string Reason) Verdict(IReadOnlyList<WorktreeCheckout> checkouts, Workspace workspace)
    {
        var checkout = checkouts.Single(c => c.WorkspaceId == workspace.Id);
        return (checkout.Verdict, checkout.Reason);
    }

    private sealed class OpenTerminals : ISessionTerminalCleanup
    {
        public HashSet<string> Open { get; } = [];
        public Task EndSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public bool HasOpenTerminal(string sessionId) => Open.Contains(sessionId);
    }
}
