using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Progress;

public sealed class SessionProgressTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 24, 0, TimeSpan.Zero);

    private static TodoEntry Todo(string content, string status) => new() { Content = content, Status = status };

    [Fact]
    public void A_first_todo_list_counts_completed_items_and_picks_the_one_in_progress()
    {
        var progress = SessionProgressTracker.ApplyTodos(
            current: null,
            "fleet-1",
            "user-1",
            [
                Todo("Write DROP TABLE statements", TodoStatuses.Completed),
                Todo("Drop the associated indexes", TodoStatuses.InProgress),
                Todo("Start the app on a fresh database", TodoStatuses.Pending),
            ],
            Now);

        progress.ShouldNotBeNull();
        (progress.SessionId, progress.UserId, progress.Kind).ShouldBe(("fleet-1", "user-1", SessionProgressKinds.Todos));
        (progress.Done, progress.Total, progress.Current).ShouldBe((1, 3, "Drop the associated indexes"));
        progress.Todos.Count.ShouldBe(3);
        progress.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void With_nothing_in_progress_the_current_item_is_the_first_pending_one()
    {
        var progress = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("Done already", TodoStatuses.Completed), Todo("Next up", TodoStatuses.Pending), Todo("Later", TodoStatuses.Pending)],
            Now);

        progress!.Current.ShouldBe("Next up");
    }

    [Fact]
    public void When_everything_is_done_there_is_no_current_item()
    {
        var progress = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.Completed), Todo("Two", TodoStatuses.Completed)],
            Now);

        (progress!.Done, progress.Total, progress.Current).ShouldBe((2, 2, (string?)null));
    }

    [Fact]
    public void Cancelled_items_are_kept_but_not_counted()
    {
        var progress = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("Kept", TodoStatuses.Completed), Todo("Dropped", TodoStatuses.Cancelled), Todo("Open", TodoStatuses.Pending)],
            Now);

        (progress!.Done, progress.Total).ShouldBe((1, 2));
        progress.Todos.Count.ShouldBe(3);
    }

    [Fact]
    public void An_updated_list_replaces_the_old_one()
    {
        var first = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.InProgress), Todo("Two", TodoStatuses.Pending)],
            Now)!;

        var second = SessionProgressTracker.ApplyTodos(
            first, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.Completed), Todo("Two", TodoStatuses.InProgress)],
            Now.AddMinutes(2));

        second.ShouldNotBeNull();
        (second.Done, second.Total, second.Current).ShouldBe((1, 2, "Two"));
        second.UpdatedAt.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void The_same_list_again_changes_nothing()
    {
        var first = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.InProgress)],
            Now)!;

        SessionProgressTracker.ApplyTodos(first, "fleet-1", "user-1", [Todo("One", TodoStatuses.InProgress)], Now.AddMinutes(1))
            .ShouldBeNull();
    }

    [Fact]
    public void An_emptied_list_clears_the_counts()
    {
        var first = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.Completed)],
            Now)!;

        var cleared = SessionProgressTracker.ApplyTodos(first, "fleet-1", "user-1", [], Now.AddMinutes(1));

        cleared.ShouldNotBeNull();
        (cleared.Done, cleared.Total, cleared.Current).ShouldBe((0, 0, (string?)null));
        cleared.Todos.ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_list_for_a_session_with_no_progress_changes_nothing()
    {
        SessionProgressTracker.ApplyTodos(null, "fleet-1", "user-1", [], Now).ShouldBeNull();
    }

    [Fact]
    public void The_summary_and_detail_carry_the_counts()
    {
        var progress = SessionProgressTracker.ApplyTodos(
            null, "fleet-1", "user-1",
            [Todo("One", TodoStatuses.Completed), Todo("Two", TodoStatuses.InProgress)],
            Now)!;

        SessionProgressTracker.ToSummary(progress).ShouldBe(new("fleet-1", SessionProgressKinds.Todos, 1, 2, "Two"));
        var detail = SessionProgressTracker.ToDto(progress);
        (detail.SessionId, detail.Done, detail.Total, detail.Current).ShouldBe(("fleet-1", 1, 2, "Two"));
        detail.Todos.Select(todo => todo.Content).ShouldBe(["One", "Two"]);
        detail.UpdatedAt.ShouldBe("2026-09-13T12:24:00.0000000Z");
    }
}
