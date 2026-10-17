using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>The turn flag on <c>sessions</c> and the interruptions a restart leaves (<c>session_interruptions</c>), on a real database.</summary>
public sealed class SessionInterruptionRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;
    private static readonly DateTimeOffset Started = new(2026, 10, 17, 14, 2, 11, TimeSpan.Zero);

    [Fact]
    public async Task A_started_turn_is_flagged_until_it_ends()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionInterruptionRepository(factory, new TestUserContext(OwnerId));

        await repository.FlagTurnAsync(session.Id, Started, waiting: false);
        var flagged = (await repository.ListFlaggedAsync()).ShouldHaveSingleItem();
        flagged.SessionId.ShouldBe(session.Id);
        flagged.StartedAt.ShouldBe(Started);
        flagged.Waiting.ShouldBeFalse();

        await repository.SetWaitingAsync(session.Id, true);
        (await repository.ListFlaggedAsync()).ShouldHaveSingleItem().Waiting.ShouldBeTrue();

        await repository.ClearTurnAsync(session.Id);
        (await repository.ListFlaggedAsync()).ShouldBeEmpty();

        // Waiting on a session that isn't mid-turn flags nothing.
        await repository.SetWaitingAsync(session.Id, true);
        (await repository.ListFlaggedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interruption_comes_back_as_saved_and_is_removed_once()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionInterruptionRepository(factory, new TestUserContext(OwnerId));

        await repository.SaveAsync(new SessionInterruption
        {
            SessionId = session.Id,
            UserId = OwnerId,
            At = Started,
            LastCall = "bash",
            ChangedFiles = 3,
        });

        var saved = (await repository.GetAsync(session.Id)).ShouldNotBeNull();
        saved.At.ShouldBe(Started);
        saved.Waiting.ShouldBeFalse();
        saved.LastCall.ShouldBe("bash");
        saved.ChangedFiles.ShouldBe(3);
        (await repository.ListForUserAsync()).ShouldHaveSingleItem();

        (await repository.RemoveAsync(session.Id)).ShouldNotBeNull();
        (await repository.RemoveAsync(session.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Another_users_session_has_none()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var owner = new SessionInterruptionRepository(factory, new TestUserContext(OwnerId));
        var other = new SessionInterruptionRepository(factory, new TestUserContext("someone-else"));
        await owner.SaveAsync(new SessionInterruption { SessionId = session.Id, UserId = OwnerId, At = Started });

        (await other.GetAsync(session.Id)).ShouldBeNull();
        (await other.ListForUserAsync()).ShouldBeEmpty();
        (await other.RemoveAsync(session.Id)).ShouldBeNull();
        (await owner.GetAsync(session.Id)).ShouldNotBeNull();
    }
}
