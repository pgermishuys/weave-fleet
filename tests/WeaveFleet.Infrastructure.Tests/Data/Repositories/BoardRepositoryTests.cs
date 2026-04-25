using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class BoardRepositoryTests
{
    [Fact]
    public async Task BoardCrud_RespectsUserScope()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board Alpha");

        await repo.InsertAsync(board);

        var retrieved = await repo.GetByIdAsync(board.Id, TestUserContext.DefaultUserId);
        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe("Board Alpha");
        retrieved.UserId.ShouldBe(TestUserContext.DefaultUserId);

        var listedBoards = await repo.ListByUserAsync(TestUserContext.DefaultUserId);
        listedBoards.Select(item => item.Id).ShouldBe(new[] { board.Id });

        board.Name = "Board Beta";
        board.UpdatedAt = UtcNow();
        await repo.UpdateAsync(board);

        var updated = await repo.GetByIdAsync(board.Id, TestUserContext.DefaultUserId);
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("Board Beta");

        var otherUserBoard = await repo.GetByIdAsync(board.Id, "other-user");
        otherUserBoard.ShouldBeNull();

        var deletedByOtherUser = await repo.DeleteAsync(board.Id, "other-user");
        deletedByOtherUser.ShouldBeFalse();

        var deleted = await repo.DeleteAsync(board.Id, TestUserContext.DefaultUserId);
        deleted.ShouldBeTrue();

        var afterDelete = await repo.GetByIdAsync(board.Id, TestUserContext.DefaultUserId);
        afterDelete.ShouldBeNull();
    }

    [Fact]
    public async Task SourceCrud_RespectsUserScope()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var source = CreateSource(board.Id, "github", "{\"repository\":\"weave/fleet\"}");
        await repo.InsertSourceAsync(source);

        var insertedSources = await repo.GetSourcesByBoardIdAsync(board.Id, TestUserContext.DefaultUserId);
        insertedSources.Count.ShouldBe(1);
        insertedSources[0].Id.ShouldBe(source.Id);
        insertedSources[0].ProviderType.ShouldBe("github");
        insertedSources[0].Config.ShouldBe("{\"repository\":\"weave/fleet\"}");
        insertedSources[0].LastSyncAt.ShouldBeNull();

        var otherUserSources = await repo.GetSourcesByBoardIdAsync(board.Id, "other-user");
        otherUserSources.ShouldBeEmpty();

        source.ProviderType = "github-enterprise";
        source.Config = "{\"repository\":\"weave/fleet\",\"label\":\"bug\"}";
        source.LastSyncAt = UtcNow();
        source.UpdatedAt = UtcNow();
        await repo.UpdateSourceAsync(source);

        var updatedSources = await repo.GetSourcesByBoardIdAsync(board.Id, TestUserContext.DefaultUserId);
        updatedSources.Count.ShouldBe(1);
        updatedSources[0].ProviderType.ShouldBe("github-enterprise");
        updatedSources[0].Config.ShouldBe("{\"repository\":\"weave/fleet\",\"label\":\"bug\"}");
        updatedSources[0].LastSyncAt.ShouldBe(source.LastSyncAt);

        var deletedByOtherUser = await repo.DeleteSourceAsync(board.Id, source.Id, "other-user");
        deletedByOtherUser.ShouldBeFalse();

        var deleted = await repo.DeleteSourceAsync(board.Id, source.Id, TestUserContext.DefaultUserId);
        deleted.ShouldBeTrue();

        var remainingSources = await repo.GetSourcesByBoardIdAsync(board.Id, TestUserContext.DefaultUserId);
        remainingSources.ShouldBeEmpty();
    }

    [Fact]
    public async Task InsertSourceAsync_WhenBoardBelongsToAnotherUser_DoesNotPersistSource()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var otherUserRepo = new BoardRepository(CreateFactory(keeper), new TestUserContext("other-user"));
        var source = CreateSource(board.Id, "github", "{}");

        await otherUserRepo.InsertSourceAsync(source);

        var sources = await repo.GetSourcesByBoardIdAsync(board.Id, TestUserContext.DefaultUserId);
        sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task LaneOperations_MaintainSingleInboxAndRebalanceOrder()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var laneOne = CreateLane(board.Id, "Todo", false);
        var laneTwo = CreateLane(board.Id, "Inbox", true);
        var laneThree = CreateLane(board.Id, "Doing", false);

        await repo.InsertLaneAsync(laneOne);
        await repo.InsertLaneAsync(laneTwo);
        await repo.InsertLaneAsync(laneThree);

        var insertedLanes = await repo.ListLanesAsync(board.Id, TestUserContext.DefaultUserId);
        insertedLanes.Count.ShouldBe(3);
        insertedLanes[0].Id.ShouldBe(laneOne.Id);
        insertedLanes[1].Id.ShouldBe(laneTwo.Id);
        insertedLanes[2].Id.ShouldBe(laneThree.Id);
        insertedLanes[0].Position.ShouldBe(1_024);
        insertedLanes[1].Position.ShouldBe(2_048);
        insertedLanes[2].Position.ShouldBe(3_072);
        insertedLanes.Count(item => item.IsInbox).ShouldBe(1);
        insertedLanes.Single(item => item.IsInbox).Id.ShouldBe(laneTwo.Id);

        var updatedLaneOne = new BoardLane
        {
            Id = laneOne.Id,
            BoardId = laneOne.BoardId,
            Name = "Todo Updated",
            Position = insertedLanes.Single(item => item.Id == laneOne.Id).Position,
            IsInbox = true,
            CreatedAt = laneOne.CreatedAt,
            UpdatedAt = UtcNow()
        };

        await repo.UpdateLaneAsync(updatedLaneOne);

        var lanesAfterUpdate = await repo.ListLanesAsync(board.Id, TestUserContext.DefaultUserId);
        lanesAfterUpdate.Count(item => item.IsInbox).ShouldBe(1);
        lanesAfterUpdate.Single(item => item.IsInbox).Id.ShouldBe(laneOne.Id);
        lanesAfterUpdate.Single(item => item.Id == laneOne.Id).Name.ShouldBe("Todo Updated");

        var setInboxResult = await repo.SetInboxLaneAsync(board.Id, laneThree.Id, TestUserContext.DefaultUserId);
        setInboxResult.ShouldBeTrue();

        var missingInboxResult = await repo.SetInboxLaneAsync(board.Id, "missing-lane", TestUserContext.DefaultUserId);
        missingInboxResult.ShouldBeFalse();

        var lanesAfterToggle = await repo.ListLanesAsync(board.Id, TestUserContext.DefaultUserId);
        lanesAfterToggle.Count(item => item.IsInbox).ShouldBe(1);
        lanesAfterToggle.Single(item => item.IsInbox).Id.ShouldBe(laneThree.Id);

        await repo.ReorderLanesAsync(board.Id, TestUserContext.DefaultUserId, new[] { laneThree.Id, laneOne.Id });

        var reorderedLanes = await repo.ListLanesAsync(board.Id, TestUserContext.DefaultUserId);
        reorderedLanes.Count.ShouldBe(3);
        reorderedLanes[0].Id.ShouldBe(laneThree.Id);
        reorderedLanes[1].Id.ShouldBe(laneOne.Id);
        reorderedLanes[2].Id.ShouldBe(laneTwo.Id);
        reorderedLanes[0].Position.ShouldBe(1_024);
        reorderedLanes[1].Position.ShouldBe(2_048);
        reorderedLanes[2].Position.ShouldBe(3_072);
    }

    [Fact]
    public async Task DeleteLaneAsync_WhenLaneContainsCards_ReturnsFalse()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var lane = CreateLane(board.Id, "Inbox", true);
        await repo.InsertLaneAsync(lane);

        var card = CreateCard(board.Id, lane.Id, "Card");
        await repo.InsertCardAsync(card);

        var deleted = await repo.DeleteLaneAsync(board.Id, lane.Id, TestUserContext.DefaultUserId);
        deleted.ShouldBeFalse();

        var persistedLane = await repo.GetLaneByIdAsync(board.Id, lane.Id, TestUserContext.DefaultUserId);
        persistedLane.ShouldNotBeNull();

        var cards = await repo.ListCardsAsync(board.Id, TestUserContext.DefaultUserId);
        cards.Select(item => item.Id).ShouldBe(new[] { card.Id });
    }

    [Fact]
    public async Task CardOperations_MoveReorderArchiveAndDelete_RebalancePositions()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var laneOne = CreateLane(board.Id, "Todo", true);
        var laneTwo = CreateLane(board.Id, "Doing", false);
        await repo.InsertLaneAsync(laneOne);
        await repo.InsertLaneAsync(laneTwo);

        var cardOne = CreateCard(board.Id, laneOne.Id, "Card One");
        var cardTwo = CreateCard(board.Id, laneOne.Id, "Card Two");
        await repo.InsertCardAsync(cardOne);
        await repo.InsertCardAsync(cardTwo);

        var insertedCards = await repo.ListCardsAsync(board.Id, TestUserContext.DefaultUserId);
        insertedCards.Count.ShouldBe(2);
        insertedCards[0].Id.ShouldBe(cardOne.Id);
        insertedCards[1].Id.ShouldBe(cardTwo.Id);
        insertedCards[0].Position.ShouldBe(1_024);
        insertedCards[1].Position.ShouldBe(2_048);

        var persistedCardOne = await repo.GetCardByIdAsync(board.Id, cardOne.Id, TestUserContext.DefaultUserId);
        persistedCardOne.ShouldNotBeNull();

        var updatedCardOne = new BoardCard
        {
            Id = persistedCardOne.Id,
            BoardId = persistedCardOne.BoardId,
            LaneId = persistedCardOne.LaneId,
            Title = "Card One Updated",
            SourceType = persistedCardOne.SourceType,
            SourceKey = persistedCardOne.SourceKey,
            Metadata = persistedCardOne.Metadata,
            Position = persistedCardOne.Position,
            ArchivedAt = persistedCardOne.ArchivedAt,
            CreatedAt = persistedCardOne.CreatedAt,
            UpdatedAt = UtcNow()
        };

        await repo.UpdateCardAsync(updatedCardOne);

        var renamedCard = await repo.GetCardByIdAsync(board.Id, cardOne.Id, TestUserContext.DefaultUserId);
        renamedCard.ShouldNotBeNull();
        renamedCard.Title.ShouldBe("Card One Updated");

        var reorderedCard = await repo.ReorderCardAsync(board.Id, cardOne.Id, 1, TestUserContext.DefaultUserId);
        reorderedCard.ShouldNotBeNull();

        var cardsAfterReorder = await repo.ListCardsAsync(board.Id, TestUserContext.DefaultUserId);
        var laneOneCardsAfterReorder = cardsAfterReorder.Where(item => item.LaneId == laneOne.Id).ToList();
        laneOneCardsAfterReorder.Count.ShouldBe(2);
        laneOneCardsAfterReorder[0].Id.ShouldBe(cardTwo.Id);
        laneOneCardsAfterReorder[1].Id.ShouldBe(cardOne.Id);
        laneOneCardsAfterReorder[0].Position.ShouldBe(1_024);
        laneOneCardsAfterReorder[1].Position.ShouldBe(2_048);

        var movedCard = await repo.MoveCardAsync(board.Id, cardOne.Id, laneTwo.Id, 0, TestUserContext.DefaultUserId);
        movedCard.ShouldNotBeNull();
        movedCard.LaneId.ShouldBe(laneTwo.Id);

        var cardThree = CreateCard(board.Id, laneTwo.Id, "Card Three");
        await repo.InsertCardAsync(cardThree);

        var archivedCard = await repo.ArchiveCardAsync(board.Id, cardOne.Id, TestUserContext.DefaultUserId, UtcNow());
        archivedCard.ShouldNotBeNull();
        archivedCard.ArchivedAt.ShouldNotBeNullOrWhiteSpace();

        var cardsAfterArchive = await repo.ListCardsAsync(board.Id, TestUserContext.DefaultUserId);
        cardsAfterArchive.Select(item => item.Id).ShouldBe(new[] { cardTwo.Id, cardThree.Id, cardOne.Id });
        cardsAfterArchive.Single(item => item.Id == cardTwo.Id).Position.ShouldBe(1_024);
        cardsAfterArchive.Single(item => item.Id == cardThree.Id).Position.ShouldBe(1_024);
        cardsAfterArchive.Single(item => item.Id == cardOne.Id).ArchivedAt.ShouldNotBeNullOrWhiteSpace();

        var deletedActiveCard = await repo.DeleteCardAsync(board.Id, cardThree.Id, TestUserContext.DefaultUserId);
        deletedActiveCard.ShouldBeTrue();

        var deletedArchivedCard = await repo.DeleteCardAsync(board.Id, cardOne.Id, TestUserContext.DefaultUserId);
        deletedArchivedCard.ShouldBeTrue();

        var deletedMissingCard = await repo.DeleteCardAsync(board.Id, "missing-card", TestUserContext.DefaultUserId);
        deletedMissingCard.ShouldBeFalse();

        var remainingCards = await repo.ListCardsAsync(board.Id, TestUserContext.DefaultUserId);
        remainingCards.Select(item => item.Id).ShouldBe(new[] { cardTwo.Id });
    }

    [Fact]
    public async Task CardOperations_WhenArchivedOrTargetLaneMissing_ReturnNull()
    {
        var (keeper, repo) = await CreateAsync();
        using var _ = keeper;

        var board = CreateBoard("Board");
        await repo.InsertAsync(board);

        var laneOne = CreateLane(board.Id, "Todo", true);
        var laneTwo = CreateLane(board.Id, "Doing", false);
        await repo.InsertLaneAsync(laneOne);
        await repo.InsertLaneAsync(laneTwo);

        var card = CreateCard(board.Id, laneOne.Id, "Card");
        await repo.InsertCardAsync(card);

        var movedToMissingLane = await repo.MoveCardAsync(board.Id, card.Id, "missing-lane", 0, TestUserContext.DefaultUserId);
        movedToMissingLane.ShouldBeNull();

        var archived = await repo.ArchiveCardAsync(board.Id, card.Id, TestUserContext.DefaultUserId, UtcNow());
        archived.ShouldNotBeNull();

        var movedArchivedCard = await repo.MoveCardAsync(board.Id, card.Id, laneTwo.Id, 0, TestUserContext.DefaultUserId);
        movedArchivedCard.ShouldBeNull();

        var reorderedArchivedCard = await repo.ReorderCardAsync(board.Id, card.Id, 0, TestUserContext.DefaultUserId);
        reorderedArchivedCard.ShouldBeNull();
    }

    private static async Task<(SqliteConnection Keeper, BoardRepository Repo)> CreateAsync()
        => await CreateAsync(TestUserContext.DefaultUserId);

    private static async Task<(SqliteConnection Keeper, BoardRepository Repo)> CreateAsync(string userId)
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var repo = new BoardRepository(factory, new TestUserContext(userId));
        return (keeper, repo);
    }

    private static TestDbHelper.SharedCacheFactory CreateFactory(SqliteConnection keeper)
        => new(keeper.ConnectionString);

    private static Board CreateBoard(string name)
        => CreateBoard(Guid.NewGuid().ToString(), TestUserContext.DefaultUserId, name);

    private static Board CreateBoard(string id, string userId, string name)
        => new()
        {
            Id = id,
            UserId = userId,
            Name = name,
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };

    private static BoardLane CreateLane(string boardId, string name, bool isInbox)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            BoardId = boardId,
            Name = name,
            Position = 0,
            IsInbox = isInbox,
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };

    private static BoardSource CreateSource(string boardId, string providerType, string config)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            BoardId = boardId,
            ProviderType = providerType,
            Config = config,
            LastSyncAt = null,
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };

    private static BoardCard CreateCard(string boardId, string laneId, string title)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            BoardId = boardId,
            LaneId = laneId,
            Title = title,
            Position = 0,
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };

    private static string UtcNow()
        => DateTime.UtcNow.ToString("O");
}
