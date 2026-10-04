using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// <see cref="DelegationService"/> as the one writer of a session's running work: what harnesses report as
/// <c>work.started</c>, <c>work.updated</c> and <c>work.ended</c>, what clients hear, and what Fleet shows.
/// </summary>
public sealed class RunningWorkTests
{
    private const string Parent = "parent-1";

    private readonly InMemoryDelegationRepository _work = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly FakeEventBroadcaster _broadcasts = new();
    private readonly SessionActivityTracker _tracker = new();
    private readonly DelegationService _sut;

    public RunningWorkTests()
    {
        _sut = new DelegationService(
            _work,
            _broadcasts,
            new TestUserContext("user-1"),
            sessionActivityWriteService: null,
            _tracker,
            _sessions,
            capabilitiesResolver: null);
    }

    private static WorkReport Shell(string id = "sh_1") => new()
    {
        WorkId = id,
        Kind = WorkKinds.Shell,
        Title = "shell",
        Label = "bun run test:e2e",
        ToolCallId = "call_shell",
        Background = true,
        CanStop = true,
        CanReadOutput = true,
    };

    private static WorkReport Subagent(string? child = null, bool? background = null) => new()
    {
        WorkId = "call_sub",
        Kind = WorkKinds.Subagent,
        Title = "code-reviewer",
        Label = "Review the diff",
        ToolCallId = "call_sub",
        Background = background,
        CanStop = child is null ? null : true,
    };

    [Fact]
    public async Task A_background_shell_starts_running_work_and_both_topics_hear_of_it()
    {
        var item = await _sut.HandleWorkReportedAsync(Parent, Shell());

        item.Kind.ShouldBe(WorkKinds.Shell);
        item.WorkId.ShouldBe("sh_1");
        item.Title.ShouldBe("shell");
        item.Label.ShouldBe("bun run test:e2e");
        item.Status.ShouldBe("running");
        item.Background.ShouldBeTrue();
        item.CanStop.ShouldBeTrue();
        item.CanReadOutput.ShouldBeTrue();
        item.ToolCallId.ShouldBe("call_shell");
        item.EndedAt.ShouldBeNull();

        // The session's conversation and every client's status bar hear it; it isn't a subagent, so no delegation event.
        _broadcasts.Broadcasts.Select(b => (b.Topic, b.Type)).ShouldBe(
        [
            ($"session:{Parent}", EventTypes.WorkStarted),
            ("sessions", EventTypes.WorkStarted),
        ]);
        var payload = _broadcasts.Broadcasts[0].Payload;
        payload.GetProperty("id").GetString().ShouldBe(item.Id);
        payload.GetProperty("sessionId").GetString().ShouldBe(Parent);
        payload.GetProperty("kind").GetString().ShouldBe("shell");
        payload.GetProperty("canStop").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_report_changes_only_what_it_sets()
    {
        await _sut.HandleWorkReportedAsync(Parent, Shell());
        _broadcasts.Broadcasts.Clear();

        var item = await _sut.HandleWorkReportedAsync(Parent, new WorkReport { WorkId = "sh_1", Detail = "3 events" });

        item.Label.ShouldBe("bun run test:e2e");
        item.CanStop.ShouldBeTrue();
        item.Detail.ShouldBe("3 events");
        _work.All.ShouldHaveSingleItem();
        _broadcasts.Broadcasts.Select(b => b.Type).ShouldAllBe(type => type == EventTypes.WorkUpdated);
    }

    [Fact]
    public async Task A_report_that_changes_nothing_tells_nobody()
    {
        await _sut.HandleWorkReportedAsync(Parent, Shell());
        _broadcasts.Broadcasts.Clear();

        await _sut.HandleWorkReportedAsync(Parent, Shell());

        _broadcasts.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ended_work_keeps_its_result_and_stays_ended()
    {
        await _sut.HandleWorkReportedAsync(Parent, Shell());

        var ended = await _sut.HandleWorkEndedAsync(Parent, "sh_1", WorkEndedReasons.Completed, "exit 0");
        // A late report, or the harness's own end after Fleet's stop, changes nothing.
        var late = await _sut.HandleWorkEndedAsync(Parent, "sh_1", WorkEndedReasons.Error, "exit 1");
        await _sut.HandleWorkReportedAsync(Parent, Shell() with { Label = "something else" });

        ended.ShouldNotBeNull();
        ended.Status.ShouldBe("completed");
        ended.EndedReason.ShouldBe(WorkEndedReasons.Completed);
        ended.Detail.ShouldBe("exit 0");
        ended.EndedAt.ShouldNotBeNull();
        late.ShouldNotBeNull().EndedReason.ShouldBe(WorkEndedReasons.Completed);
        _work.All.ShouldHaveSingleItem().Label.ShouldBe("bun run test:e2e");
        _broadcasts.Broadcasts.Count(b => b.Type == EventTypes.WorkEnded).ShouldBe(2); // once per topic
    }

    [Fact]
    public async Task Lost_work_ends_cancelled_and_says_it_was_lost()
    {
        await _sut.HandleWorkReportedAsync(Parent, Shell());

        var ended = await _sut.HandleWorkEndedAsync(Parent, "sh_1", WorkEndedReasons.Lost);

        ended.ShouldNotBeNull();
        ended.Status.ShouldBe("cancelled");
        ended.EndedReason.ShouldBe(WorkEndedReasons.Lost);
    }

    [Fact]
    public async Task An_end_for_work_fleet_never_heard_of_is_ignored()
    {
        (await _sut.HandleWorkEndedAsync(Parent, "sh_unknown", WorkEndedReasons.Completed)).ShouldBeNull();
        _broadcasts.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_subagent_is_a_delegation_too_and_its_card_hears_of_it()
    {
        var item = await _sut.HandleWorkReportedAsync(Parent, Subagent());

        item.Kind.ShouldBe(WorkKinds.Subagent);
        item.Status.ShouldBe("pending");
        item.Label.ShouldBe("Review the diff");
        item.CanStop.ShouldBeFalse();
        _broadcasts.Broadcasts.Select(b => b.Type).ShouldBe(["delegation.created", EventTypes.WorkStarted, EventTypes.WorkStarted]);
        (await _sut.GetDelegationsAsync(Parent)).ShouldHaveSingleItem().ParentToolCallId.ShouldBe("call_sub");
    }

    [Fact]
    public async Task A_subagents_child_session_links_it_and_counts_as_its_parents_work_until_it_goes_to_the_background()
    {
        await _sut.HandleWorkReportedAsync(Parent, Subagent());

        var linked = await _sut.HandleWorkReportedAsync(Parent, Subagent(child: "ses_child") with { ChildHarnessSessionId = "ses_child" }, childSessionId: "fleet-child");

        linked.Status.ShouldBe("running");
        linked.ChildSessionId.ShouldBe("fleet-child");
        linked.CanStop.ShouldBeTrue();
        _tracker.GetParentSessionId("fleet-child").ShouldBe(Parent);
        _tracker.IsChildInBackground("fleet-child").ShouldBeFalse();
        _broadcasts.Broadcasts.ShouldContain(b => b.Type == "delegation.updated" && b.Payload.GetProperty("childSessionId").GetString() == "fleet-child");

        var backgrounded = await _sut.HandleWorkReportedAsync(Parent, Subagent(background: true));

        backgrounded.Background.ShouldBeTrue();
        _tracker.IsChildInBackground("fleet-child").ShouldBeTrue();

        var ended = await _sut.HandleWorkEndedAsync(Parent, "call_sub", WorkEndedReasons.Completed);

        ended.ShouldNotBeNull().Status.ShouldBe("completed");
        _tracker.GetParentSessionId("fleet-child").ShouldBeNull();
    }

    [Fact]
    public async Task Work_whose_harness_no_longer_runs_it_ends_lost_and_work_it_still_runs_is_left_alone()
    {
        await _sut.HandleWorkReportedAsync(Parent, Shell("sh_gone"));
        await _sut.HandleWorkReportedAsync(Parent, Shell("sh_still"));
        _sessions.Seed(new Session { Id = "fleet-child", OpencodeSessionId = "ses_child", UserId = "user-1" });
        await _sut.HandleWorkReportedAsync(Parent, Subagent(), childSessionId: "fleet-child");

        // The harness lost track of which call started its child, but it still runs the child.
        var lost = await _sut.SettleLostWorkAsync(Parent,
        [
            new WorkReport { WorkId = "sh_still" },
            new WorkReport { WorkId = "ses_child", ChildHarnessSessionId = "ses_child" },
        ]);

        lost.ShouldBe(1);
        _work.All.Single(w => w.WorkId == "sh_gone").EndedReason.ShouldBe(WorkEndedReasons.Lost);
        _work.All.Single(w => w.WorkId == "sh_still").IsRunning.ShouldBeTrue();
        _work.All.Single(w => w.WorkId == "call_sub").IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task The_sessions_work_is_what_runs_and_what_ended_lately_unless_all_is_asked_for()
    {
        var old = DateTime.UtcNow.AddHours(-1).ToString("O");
        _work.Seed(new Delegation
        {
            Id = "old",
            ParentSessionId = Parent,
            ParentToolCallId = "call_old",
            Title = "explore",
            Status = "completed",
            CreatedAt = old,
            UpdatedAt = old,
            CompletedAt = old,
            WorkId = "call_old",
        });
        await _sut.HandleWorkReportedAsync(Parent, Shell("sh_running"));
        await _sut.HandleWorkReportedAsync(Parent, Shell("sh_done"));
        await _sut.HandleWorkEndedAsync(Parent, "sh_done", WorkEndedReasons.Completed, "exit 0");

        (await _sut.GetWorkAsync(Parent)).Select(w => w.WorkId).ShouldBe(["sh_running", "sh_done"], ignoreOrder: true);
        (await _sut.GetWorkAsync(Parent, all: true)).Select(w => w.WorkId).ShouldBe(["call_old", "sh_running", "sh_done"], ignoreOrder: true);
        (await _sut.GetAllRunningWorkAsync()).ShouldHaveSingleItem().WorkId.ShouldBe("sh_running");
    }

    [Fact]
    public void A_delegation_from_before_running_work_reads_as_a_subagent()
    {
        // What migration 048 leaves of a delegation row: a subagent, known by its call, ended the way its status says.
        var item = DelegationService.ToItem(new Delegation
        {
            Id = "del-1",
            ParentSessionId = Parent,
            ParentToolCallId = "call_1",
            ChildSessionId = "child-1",
            Title = "general",
            Status = "error",
            CreatedAt = "2026-09-01T10:00:00.0000000Z",
            UpdatedAt = "2026-09-01T10:05:00.0000000Z",
            CompletedAt = "2026-09-01T10:05:00.0000000Z",
        });

        item.Kind.ShouldBe(WorkKinds.Subagent);
        item.WorkId.ShouldBe("call_1");
        item.EndedReason.ShouldBe("error");
        item.EndedAt.ShouldBe("2026-09-01T10:05:00.0000000Z");
        item.StartedAt.ShouldBe("2026-09-01T10:00:00.0000000Z");
        item.CanStop.ShouldBeFalse();
    }
}
