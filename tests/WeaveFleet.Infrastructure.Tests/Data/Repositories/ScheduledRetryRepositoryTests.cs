using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>The turns Fleet tries again (<c>scheduled_retries</c>), on a real database.</summary>
public sealed class ScheduledRetryRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;
    private static readonly DateTimeOffset Due = new(2026, 10, 7, 3, 44, 14, TimeSpan.Zero);

    private static ScheduledRetry Retry(string sessionId, int attempt = 1, string state = ScheduledRetryStates.Waiting) => new()
    {
        SessionId = sessionId,
        UserId = OwnerId,
        DueAt = Due,
        Attempt = attempt,
        Kind = TurnErrorKinds.UsageLimit,
        Reason = "You've hit your session limit · resets 3:43am (UTC)",
        ProviderSaid = true,
        State = state,
        CreatedAt = new DateTimeOffset(2026, 10, 7, 1, 51, 43, TimeSpan.Zero),
    };

    [Fact]
    public async Task A_retry_comes_back_as_saved_and_a_new_one_replaces_it()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new ScheduledRetryRepository(factory, new TestUserContext(OwnerId));

        (await repository.SaveAsync(Retry(session.Id))).ShouldBeTrue();
        (await repository.SaveAsync(Retry(session.Id, attempt: 2))).ShouldBeTrue();

        var retry = (await repository.GetAsync(session.Id)).ShouldNotBeNull();
        retry.Attempt.ShouldBe(2);
        retry.DueAt.ShouldBe(Due);
        retry.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        retry.Reason.ShouldBe("You've hit your session limit · resets 3:43am (UTC)");
        retry.ProviderSaid.ShouldBeTrue();
        retry.State.ShouldBe(ScheduledRetryStates.Waiting);
        (await repository.ListWaitingAsync()).ShouldHaveSingleItem();
        (await repository.ListForUserAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_waiting_retry_is_taken_once_and_then_reads_as_sent()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new ScheduledRetryRepository(factory, new TestUserContext(OwnerId));
        await repository.SaveAsync(Retry(session.Id));

        (await repository.TakeWaitingAsync(session.Id)).ShouldNotBeNull().State.ShouldBe(ScheduledRetryStates.Sent);
        (await repository.TakeWaitingAsync(session.Id)).ShouldBeNull();
        (await repository.ListWaitingAsync()).ShouldBeEmpty();
        (await repository.RemoveAsync(session.Id)).ShouldNotBeNull();
        (await repository.GetAsync(session.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Another_users_session_has_none_and_takes_none()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var owner = new ScheduledRetryRepository(factory, new TestUserContext(OwnerId));
        var stranger = new ScheduledRetryRepository(factory, new TestUserContext("someone-else"));
        await owner.SaveAsync(Retry(session.Id));

        (await stranger.SaveAsync(Retry(session.Id, attempt: 5))).ShouldBeFalse();
        (await stranger.GetAsync(session.Id)).ShouldBeNull();
        (await stranger.TakeWaitingAsync(session.Id)).ShouldBeNull();
        (await stranger.RemoveAsync(session.Id)).ShouldBeNull();
        (await stranger.ListForUserAsync()).ShouldBeEmpty();
        (await owner.GetAsync(session.Id)).ShouldNotBeNull().Attempt.ShouldBe(1);
    }
}
