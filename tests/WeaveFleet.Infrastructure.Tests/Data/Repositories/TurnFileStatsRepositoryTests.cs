using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class TurnFileStatsRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_KeepsEachTurnsFiles_AndListsThemOldestFirst()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repo = new TurnFileStatsRepository(factory, new TestUserContext(OwnerId));

        await repo.AddAsync(session.Id, T0.AddMinutes(5), [new TurnFileStat("README.md", 0, 3)]);
        await repo.AddAsync(session.Id, T0, [new TurnFileStat("README.md", 3, 1), new TurnFileStat("src/a.ts", 1, 0)]);

        var listed = await repo.ListAsync(session.Id);
        listed.Select(snapshot => snapshot.EndedAt).ShouldBe([T0, T0.AddMinutes(5)]);
        listed[0].Files.ShouldBe([new TurnFileStat("README.md", 3, 1), new TurnFileStat("src/a.ts", 1, 0)]);
        listed[1].Files.ShouldBe([new TurnFileStat("README.md", 0, 3)]);
    }

    [Fact]
    public async Task AddAsync_KeepsATurnThatChangedNothing_AsASnapshotWithNoFiles()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repo = new TurnFileStatsRepository(factory, new TestUserContext(OwnerId));

        await repo.AddAsync(session.Id, T0, []);

        var snapshot = (await repo.ListAsync(session.Id)).ShouldHaveSingleItem();
        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_AndAddAsync_AreScopedToTheCurrentUsersSessions()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var owner = new TurnFileStatsRepository(factory, new TestUserContext(OwnerId));
        var other = new TurnFileStatsRepository(factory, new TestUserContext("someone-else"));

        await other.AddAsync(session.Id, T0, [new TurnFileStat("stolen.txt", 1, 1)]);
        (await owner.ListAsync(session.Id)).ShouldBeEmpty();

        await owner.AddAsync(session.Id, T0, [new TurnFileStat("README.md", 1, 1)]);
        (await other.ListAsync(session.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAsync_KeepsOnlyTheNewestSnapshots()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repo = new TurnFileStatsRepository(factory, new TestUserContext(OwnerId));

        for (var i = 0; i < TurnFileStatsRepository.MaxSnapshots + 3; i++)
            await repo.AddAsync(session.Id, T0.AddSeconds(i), [new TurnFileStat("a.txt", i, 0)]);

        var listed = await repo.ListAsync(session.Id);
        listed.Count.ShouldBe(TurnFileStatsRepository.MaxSnapshots);
        listed[0].Files.Single().Additions.ShouldBe(3);
    }
}
