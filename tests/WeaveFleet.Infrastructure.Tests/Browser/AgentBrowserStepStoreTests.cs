using WeaveFleet.Application.Browser;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Browser;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class AgentBrowserStepStoreTests
{
    private const string Owner = "local-user";

    [Fact]
    public async Task Steps_are_numbered_kept_with_the_session_and_pushed_to_its_watchers()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, Owner);
        var events = new FakeEventBroadcaster();
        using var store = new AgentBrowserStepStore(factory, events);

        await store.RecordAsync(Step(session.Id, "Opened localhost:5173 in its own tab"), Owner);
        var click = await store.RecordAsync(Step(session.Id, "Clicked “Save”") with
        {
            Box = new AgentBox(326, 99, 46, 22),
            Screenshot = new ScreenshotReference(session.Id, "shot1", 1280, 800),
        }, Owner);

        click.Seq.ShouldBe(2);
        var kept = await store.ListAsync(session.Id);
        kept.Select(step => (step.Seq, step.Summary)).ShouldBe([(1L, "Opened localhost:5173 in its own tab"), (2L, "Clicked “Save”")]);
        kept[1].Box.ShouldBe(new AgentBox(326, 99, 46, 22));
        kept[1].Screenshot.ShouldBe(new ScreenshotReference(session.Id, "shot1", 1280, 800));

        var pushed = events.Broadcasts.Last();
        pushed.Topic.ShouldBe($"session:{session.Id}");
        pushed.UserId.ShouldBe(Owner);
        pushed.DomainEvent.ShouldBeOfType<BrowserStepped>().Payload.Summary.ShouldBe("Clicked “Save”");
        pushed.Payload.GetProperty("summary").GetString().ShouldBe("Clicked “Save”");
        pushed.Payload.GetProperty("box").GetProperty("width").GetDouble().ShouldBe(46);

        await store.DeleteSessionAsync(session.Id);
        (await store.ListAsync(session.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Listing_gives_the_newest_steps_oldest_first()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, Owner);
        using var store = new AgentBrowserStepStore(factory, new FakeEventBroadcaster());
        for (var i = 1; i <= 5; i++)
            await store.RecordAsync(Step(session.Id, $"Step {i}"), Owner);

        (await store.ListAsync(session.Id, limit: 2)).Select(step => step.Summary).ShouldBe(["Step 4", "Step 5"]);
    }

    private static AgentBrowserStep Step(string sessionId, string summary) => new()
    {
        SessionId = sessionId,
        At = DateTimeOffset.Parse("2026-10-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        Kind = "click",
        Summary = summary,
        Detail = "click @e2 · 170 ms",
    };
}
