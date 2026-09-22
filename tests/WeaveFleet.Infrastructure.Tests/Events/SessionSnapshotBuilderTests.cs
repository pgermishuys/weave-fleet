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
