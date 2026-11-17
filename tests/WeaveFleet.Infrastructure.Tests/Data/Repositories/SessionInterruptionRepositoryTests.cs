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
    public async Task Archiving_a_session_mid_turn_drops_its_turn_flag_so_a_later_restart_does_not_resurrect_it()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var interruptions = new SessionInterruptionRepository(factory, new TestUserContext(OwnerId));
        var sessions = new SessionRepository(factory, new TestUserContext(OwnerId));

        await interruptions.FlagTurnAsync(session.Id, Started, waiting: false);
        await sessions.ArchiveAsync(session.Id, Started.ToString("O"));
        await sessions.UnarchiveAsync(session.Id);

        // Restored later, the session must not look like a turn the next restart cut off.
        (await interruptions.ListFlaggedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_turn_is_marked_until_the_next_turn_starts()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionInterruptionRepository(factory, new TestUserContext(OwnerId));

        (await repository.ListFailuresForUserAsync()).ShouldBeEmpty();

        await repository.SetFailureAsync(session.Id, Started, "Provider returned 529: overloaded");
        var failure = (await repository.ListFailuresForUserAsync()).ShouldHaveSingleItem();
        failure.Key.ShouldBe(session.Id);
        failure.Value.At.ShouldBe(Started);
        failure.Value.Message.ShouldBe("Provider returned 529: overloaded");

        (await repository.ClearFailureAsync(session.Id)).ShouldBeTrue();
        (await repository.ClearFailureAsync(session.Id)).ShouldBeFalse();
        (await repository.ListFailuresForUserAsync()).ShouldBeEmpty();
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
