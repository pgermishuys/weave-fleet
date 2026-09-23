using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Events;

public sealed class SessionSnapshotBuilderTests
{
    // Opening a parent while its subagent waits on a question: the subagent's row has to say so from the
    // snapshot, since the child's own activity_status went out before the parent was opened.
    [Fact]
    public async Task BuildAsync_gives_each_delegation_what_its_child_shows()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var userId = TestUserContext.DefaultUserId;
        var parent = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);
        var waitingChild = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);
        var untrackedChild = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);

        var delegations = new DelegationRepository(factory, new TestUserContext());
        await delegations.InsertAsync(Delegation("waiting", parent.Session.Id, waitingChild.Session.Id));
        await delegations.InsertAsync(Delegation("untracked", parent.Session.Id, untrackedChild.Session.Id));
        await delegations.InsertAsync(Delegation("unlinked", parent.Session.Id, childSessionId: null));

        var tracker = new SessionActivityTracker();
        tracker.Update(parent.Session.Id, "busy", userId);
        tracker.Update(waitingChild.Session.Id, "waiting_input", userId);

        var snapshot = await new SessionSnapshotBuilder(factory, new TestUserContext(), tracker).BuildAsync(parent.Session.Id);

        snapshot.Delegations.Single(d => d.DelegationId == "waiting").ChildActivityStatus.ShouldBe("waiting_input");
        snapshot.Delegations.Single(d => d.DelegationId == "untracked").ChildActivityStatus.ShouldBeNull();
        snapshot.Delegations.Single(d => d.DelegationId == "unlinked").ChildActivityStatus.ShouldBeNull();
    }

    // Reopening a parent while only a background subagent works: it's free, and the row says where the child is.
    [Fact]
    public async Task BuildAsync_reads_a_parent_whose_only_work_is_in_the_background_as_idle()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var userId = TestUserContext.DefaultUserId;
        var parent = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);
        var backgroundChild = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);
        var foregroundChild = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, userId);

        var delegations = new DelegationRepository(factory, new TestUserContext());
        await delegations.InsertAsync(Delegation("background", parent.Session.Id, backgroundChild.Session.Id));
        await delegations.InsertAsync(Delegation("foreground", parent.Session.Id, foregroundChild.Session.Id));

        var tracker = new SessionActivityTracker();
        tracker.Update(parent.Session.Id, "idle", userId);
        tracker.Update(backgroundChild.Session.Id, "busy", userId);
        tracker.RegisterChild(backgroundChild.Session.Id, parent.Session.Id);
        tracker.MoveChildToBackground(backgroundChild.Session.Id);

        var builder = new SessionSnapshotBuilder(factory, new TestUserContext(), tracker);
        var snapshot = await builder.BuildAsync(parent.Session.Id);

        snapshot.ActivityStatus.ShouldBe("idle");
        snapshot.Delegations.Single(d => d.DelegationId == "background").Background.ShouldBe(true);
        snapshot.Delegations.Single(d => d.DelegationId == "foreground").Background.ShouldBeNull();

        // A foreground child at work still makes the parent busy.
        tracker.Update(foregroundChild.Session.Id, "busy", userId);
        tracker.RegisterChild(foregroundChild.Session.Id, parent.Session.Id);
        (await builder.BuildAsync(parent.Session.Id)).ActivityStatus.ShouldBe("busy");
    }

    private static Delegation Delegation(string id, string parentSessionId, string? childSessionId) => new()
    {
        Id = id,
        ParentSessionId = parentSessionId,
        ChildSessionId = childSessionId,
        ParentToolCallId = $"call-{id}",
        Title = id,
        Status = "running",
        CreatedAt = DateTime.UtcNow.ToString("O"),
        UpdatedAt = DateTime.UtcNow.ToString("O"),
    };
}
