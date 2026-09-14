using WeaveFleet.Application.Progress;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Progress;

public sealed class SessionProgressReaderTests
{
    private static readonly Session Session = new() { Id = "fleet-1", InstanceId = "instance-1", UserId = "user-1" };

    private static SessionProgress Stored(int done, int total) => new()
    {
        SessionId = Session.Id,
        UserId = Session.UserId,
        Kind = SessionProgressKinds.Todos,
        Done = done,
        Total = total,
        Current = "Next",
        Todos = [new TodoEntry { Content = "Next", Status = TodoStatuses.InProgress }],
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Returns_stored_progress()
    {
        var repository = new InMemorySessionProgressRepository();
        await repository.UpsertAsync(Stored(2, 5), CancellationToken.None);
        var reader = new SessionProgressReader(repository, new InstanceTracker());

        var progress = await reader.GetAsync(Session, CancellationToken.None);

        progress.ShouldNotBeNull();
        (progress.Done, progress.Total, progress.Current).ShouldBe((2, 5, "Next"));
    }

    [Fact]
    public async Task With_nothing_stored_asks_the_running_harness_and_stores_its_answer()
    {
        var repository = new InMemorySessionProgressRepository();
        var instances = new InstanceTracker();
        instances.Register("instance-1", new FakeHarnessSession("instance-1")
        {
            Todos =
            [
                new TodoEntry { Content = "Add the endpoint", Status = TodoStatuses.Completed },
                new TodoEntry { Content = "Write tests", Status = TodoStatuses.InProgress },
            ],
        });
        var reader = new SessionProgressReader(repository, instances);

        var progress = await reader.GetAsync(Session, CancellationToken.None);

        progress.ShouldNotBeNull();
        (progress.Done, progress.Total, progress.Current).ShouldBe((1, 2, "Write tests"));
        repository.All.ShouldHaveSingleItem().UserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task With_nothing_stored_and_no_running_harness_returns_null()
    {
        var reader = new SessionProgressReader(new InMemorySessionProgressRepository(), new InstanceTracker());

        (await reader.GetAsync(Session, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task With_nothing_stored_and_a_harness_that_cannot_report_todos_returns_null()
    {
        var repository = new InMemorySessionProgressRepository();
        var instances = new InstanceTracker();
        instances.Register("instance-1", new FakeHarnessSession("instance-1"));
        var reader = new SessionProgressReader(repository, instances);

        (await reader.GetAsync(Session, CancellationToken.None)).ShouldBeNull();
        repository.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Summaries_cover_only_sessions_with_progress()
    {
        var repository = new InMemorySessionProgressRepository();
        await repository.UpsertAsync(Stored(1, 3), CancellationToken.None);
        var reader = new SessionProgressReader(repository, new InstanceTracker());

        var summaries = await reader.GetSummariesAsync(["fleet-1", "fleet-2"], CancellationToken.None);

        summaries.Keys.ShouldBe(["fleet-1"]);
        (summaries["fleet-1"].Done, summaries["fleet-1"].Total).ShouldBe((1, 3));
    }
}
