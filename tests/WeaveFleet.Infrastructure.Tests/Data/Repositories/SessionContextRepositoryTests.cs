using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>How full each session's context is (<c>session_context</c>), on a real database.</summary>
public sealed class SessionContextRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;
    private static readonly DateTimeOffset At = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_sessions_context_comes_back_as_it_was_stored()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionContextRepository(factory, new TestUserContext(OwnerId));
        var context = new SessionContext
        {
            SessionId = session.Id,
            UserId = OwnerId,
            Used = 61_160,
            Limit = 200_000,
            CompactsAt = 167_000,
            ModelId = "claude-opus-5",
            ProviderId = "anthropic",
            LastCall = new ContextCall { Input = 1_000, CacheRead = 60_000, CacheWrite = 50, Output = 100, Reasoning = 10 },
            LastCallAt = At,
            Compacting = true,
            CompactedAt = At.AddMinutes(-5),
            CompactionError = "Not enough messages to compact.",
            Turns = [new SessionContextTurn(20_000, 200_000, At.AddMinutes(-3), AfterCompaction: true), new SessionContextTurn(61_160, 200_000, At, AfterCompaction: false)],
            UpdatedAt = At,
        };

        (await repository.UpsertAsync(context, CancellationToken.None)).ShouldBeTrue();
        var read = (await repository.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull();

        (read with { Turns = [] }).ShouldBe(context with { Turns = [] });
        read.Turns.ShouldBe(context.Turns);
    }

    [Fact]
    public async Task A_later_write_replaces_the_row_and_unknowns_stay_null()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionContextRepository(factory, new TestUserContext(OwnerId));
        await repository.UpsertAsync(new SessionContext { SessionId = session.Id, UserId = OwnerId, Used = 5, LastCall = new ContextCall { Input = 5 }, UpdatedAt = At }, CancellationToken.None);

        await repository.UpsertAsync(new SessionContext { SessionId = session.Id, UserId = OwnerId, CompactedAt = At, UpdatedAt = At }, CancellationToken.None);

        var read = (await repository.GetAsync(session.Id, CancellationToken.None)).ShouldNotBeNull();
        read.Used.ShouldBeNull();
        read.LastCall.ShouldBeNull();
        read.Limit.ShouldBeNull();
        read.CompactedAt.ShouldBe(At);
        read.Turns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_sessions_owner_can_write_or_read_it()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var stranger = new SessionContextRepository(factory, new TestUserContext("someone-else"));

        (await stranger.UpsertAsync(new SessionContext { SessionId = session.Id, UserId = "someone-else", Used = 1, UpdatedAt = At }, CancellationToken.None)).ShouldBeFalse();

        var owner = new SessionContextRepository(factory, new TestUserContext(OwnerId));
        await owner.UpsertAsync(new SessionContext { SessionId = session.Id, UserId = OwnerId, Used = 1, UpdatedAt = At }, CancellationToken.None);
        (await stranger.GetAsync(session.Id, CancellationToken.None)).ShouldBeNull();
        (await owner.GetForOwnerAsync(session.Id, OwnerId, CancellationToken.None)).ShouldNotBeNull();
    }
}
