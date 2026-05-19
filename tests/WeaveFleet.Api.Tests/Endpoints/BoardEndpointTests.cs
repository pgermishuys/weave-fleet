using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Plugins;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class BoardEndpointTests
{
    [Fact]
    public async Task BoardEndpoints_FullLifecycle_ReturnsExpectedStatusCodesAndPersistsOrdering()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "My Board");
        var boardId = board.Id;

        var listBoardsResponse = await client.GetAsync("/api/boards");
        listBoardsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var boards = await listBoardsResponse.Content.ReadFromJsonAsync<BoardDto[]>(JsonSerializerOptions.Web);
        boards.ShouldNotBeNull();
        boards.ShouldContain(item => item.Id == boardId);

        var updateBoardResponse = await PatchAsJsonAsync(client, $"/api/boards/{boardId}", new { name = "Renamed Board" });
        updateBoardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedBoard = await updateBoardResponse.Content.ReadFromJsonAsync<BoardDto>(JsonSerializerOptions.Web);
        updatedBoard.ShouldNotBeNull();
        updatedBoard.Name.ShouldBe("Renamed Board");

        var laneOne = await CreateLaneAsync(client, boardId, "Inbox");
        var laneOneId = laneOne.Id;

        var laneTwo = await CreateLaneAsync(client, boardId, "Doing");
        var laneTwoId = laneTwo.Id;

        var updateLaneResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{boardId}/lanes/{laneOneId}",
            new { name = "Inbox Updated", isInbox = true });
        updateLaneResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedLane = await updateLaneResponse.Content.ReadFromJsonAsync<BoardLaneDto>(JsonSerializerOptions.Web);
        updatedLane.ShouldNotBeNull();
        updatedLane.Name.ShouldBe("Inbox Updated");
        updatedLane.IsInbox.ShouldBeTrue();

        var reorderLanesResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{boardId}/lanes/reorder",
            new { laneIds = new[] { laneTwoId, laneOneId } });
        reorderLanesResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var lanesAfterReorder = await GetBoardLanesAsync(client, boardId);
        lanesAfterReorder.Length.ShouldBe(2);
        lanesAfterReorder[0].Id.ShouldBe(laneTwoId);
        lanesAfterReorder[1].Id.ShouldBe(laneOneId);
        lanesAfterReorder[0].Position.ShouldBe(1_024);
        lanesAfterReorder[1].Position.ShouldBe(2_048);
        lanesAfterReorder.Single(item => item.Id == laneOneId).IsInbox.ShouldBeTrue();

        var cardOne = await CreateCardAsync(client, boardId, laneOneId, "Card One");
        var cardOneId = cardOne.Id;

        var cardsAfterCreate = await GetBoardCardsAsync(client, boardId);
        cardsAfterCreate.Select(item => item.Id).ShouldBe(new[] { cardOneId });

        var updateCardTitleResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{boardId}/cards/{cardOneId}",
            new { title = "Card One Updated" });
        updateCardTitleResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var renamedCard = await updateCardTitleResponse.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web);
        renamedCard.ShouldNotBeNull();
        renamedCard.Title.ShouldBe("Card One Updated");

        var moveCardResponse = await client.PostAsJsonAsync(
            $"/api/boards/{boardId}/cards/{cardOneId}/move",
            new { laneId = laneTwoId, position = 0 });
        moveCardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var movedCard = await moveCardResponse.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web);
        movedCard.ShouldNotBeNull();
        movedCard.LaneId.ShouldBe(laneTwoId);
        movedCard.Position.ShouldBe(1_024);

        var cardTwo = await CreateCardAsync(client, boardId, laneTwoId, "Card Two");
        var cardTwoId = cardTwo.Id;

        var reorderCardResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{boardId}/cards/{cardOneId}",
            new { laneId = laneTwoId, position = 1 });
        reorderCardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reorderedCard = await reorderCardResponse.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web);
        reorderedCard.ShouldNotBeNull();
        reorderedCard.Position.ShouldBe(2_048);

        var archiveCardResponse = await client.PostAsync($"/api/boards/{boardId}/cards/{cardTwoId}/archive", content: null);
        archiveCardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var archivedCard = await archiveCardResponse.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web);
        archivedCard.ShouldNotBeNull();
        archivedCard.ArchivedAt.ShouldNotBeNullOrWhiteSpace();

        var cardsAfterArchive = await GetBoardCardsAsync(client, boardId);
        cardsAfterArchive.Select(item => item.Id).ShouldBe(new[] { cardOneId, cardTwoId });
        cardsAfterArchive.Single(item => item.Id == cardOneId).Position.ShouldBe(1_024);
        cardsAfterArchive.Single(item => item.Id == cardTwoId).ArchivedAt.ShouldNotBeNullOrWhiteSpace();

        var deleteCardResponse = await client.DeleteAsync($"/api/boards/{boardId}/cards/{cardOneId}");
        deleteCardResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var deleteLaneOneResponse = await client.DeleteAsync($"/api/boards/{boardId}/lanes/{laneOneId}");
        deleteLaneOneResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var deleteBoardResponse = await client.DeleteAsync($"/api/boards/{boardId}");
        deleteBoardResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var boardsAfterDeleteResponse = await client.GetAsync("/api/boards");
        boardsAfterDeleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var boardsAfterDelete = await boardsAfterDeleteResponse.Content.ReadFromJsonAsync<BoardDto[]>(JsonSerializerOptions.Web);
        boardsAfterDelete.ShouldNotBeNull();
        boardsAfterDelete.ShouldNotContain(item => item.Id == boardId);
    }

    [Fact]
    public async Task DeleteLane_WhenLaneContainsCards_ReturnsConflict()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Board");
        var lane = await CreateLaneAsync(client, board.Id, "Inbox");

        var createCardResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards",
            new { laneId = lane.Id, title = "Card" });
        createCardResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleteLaneResponse = await client.DeleteAsync($"/api/boards/{board.Id}/lanes/{lane.Id}");
        deleteLaneResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateLane_WhenInboxToggled_ClearsPreviousInboxLane()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Board");
        var laneOne = await CreateLaneAsync(client, board.Id, "Todo");
        var laneTwo = await CreateLaneAsync(client, board.Id, "Inbox");

        var setFirstInboxResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/lanes/{laneOne.Id}",
            new { isInbox = true });
        setFirstInboxResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var setSecondInboxResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/lanes/{laneTwo.Id}",
            new { isInbox = true });
        setSecondInboxResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lanes = await GetBoardLanesAsync(client, board.Id);
        lanes.Count(item => item.IsInbox).ShouldBe(1);
        lanes.Single(item => item.IsInbox).Id.ShouldBe(laneTwo.Id);
        lanes.Single(item => item.Id == laneOne.Id).IsInbox.ShouldBeFalse();
    }

    [Fact]
    public async Task BoardEndpoints_InvalidOperations_ReturnExpectedErrors()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Board");

        var invalidLaneCreateResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/lanes",
            new { name = "Invalid", position = -1 });
        invalidLaneCreateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var laneOne = await CreateLaneAsync(client, board.Id, "Todo");
        var laneTwo = await CreateLaneAsync(client, board.Id, "Doing");

        var invalidLaneUpdateResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/lanes/{laneOne.Id}",
            new { position = -1 });
        invalidLaneUpdateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var missingLaneCardResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards",
            new { laneId = "missing-lane", title = "Card" });
        missingLaneCardResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var invalidCardCreateResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards",
            new { laneId = laneOne.Id, title = "Card", position = -1 });
        invalidCardCreateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var card = await CreateCardAsync(client, board.Id, laneOne.Id, "Card");

        var invalidCardUpdateResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/cards/{card.Id}",
            new { position = -1 });
        invalidCardUpdateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var invalidMoveResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{card.Id}/move",
            new { laneId = laneTwo.Id, position = -1 });
        invalidMoveResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var archiveCardResponse = await client.PostAsync($"/api/boards/{board.Id}/cards/{card.Id}/archive", content: null);
        archiveCardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var moveArchivedResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{card.Id}/move",
            new { laneId = laneTwo.Id, position = 0 });
        moveArchivedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var reorderArchivedResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/cards/{card.Id}",
            new { laneId = laneTwo.Id, position = 0 });
        reorderArchivedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SourceEndpoints_FullLifecycle_AndSyncReturnsExpectedCounts()
    {
        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<GitHubApiProxy>();
                services.RemoveAll<IIntegrationStore>();
                services.RemoveAll<IPluginStateStore>();
                services.RemoveAll<ICredentialProtector>();
                services.AddSingleton<IIntegrationStore, InMemoryIntegrationStore>();
                services.AddSingleton<IPluginStateStore, PluginStateStore>();
                services.AddSingleton<ICredentialProtector, FakeCredentialProtector>();
                services.AddSingleton<GitHubApiProxy>(_ => new GitHubApiProxy(new TestHttpClientFactory(new FakeGitHubIssuesHandler())));
            });
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Sync Board");
        var inboxLane = await CreateLaneAsync(client, board.Id, "Inbox");

        var setInboxResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/lanes/{inboxLane.Id}",
            new { isInbox = true });
        setInboxResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createSourceResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/sources",
            new { providerType = "github", config = "{\"repository\":\"acme/rocket\"}" });
        createSourceResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdSource = await createSourceResponse.Content.ReadFromJsonAsync<BoardSourceDto>(JsonSerializerOptions.Web);
        createdSource.ShouldNotBeNull();
        createdSource.ProviderType.ShouldBe("github");
        createdSource.Config.ShouldBe("{\"repository\":\"acme/rocket\"}");
        createdSource.LastSyncAt.ShouldBeNull();

        var listSourcesResponse = await client.GetAsync($"/api/boards/{board.Id}/sources");
        listSourcesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listedSources = await listSourcesResponse.Content.ReadFromJsonAsync<BoardSourceDto[]>(JsonSerializerOptions.Web);
        listedSources.ShouldNotBeNull();
        listedSources.Length.ShouldBe(1);
        listedSources[0].Id.ShouldBe(createdSource.Id);

        var updateSourceResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/sources/{createdSource.Id}",
            new { config = "{\"repository\":\"acme/rocket\",\"labels\":[\"bug\"]}" });
        updateSourceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedSource = await updateSourceResponse.Content.ReadFromJsonAsync<BoardSourceDto>(JsonSerializerOptions.Web);
        updatedSource.ShouldNotBeNull();
        updatedSource.Config.ShouldBe("{\"repository\":\"acme/rocket\",\"labels\":[\"bug\"]}");

        using (var scope = factory.Services.CreateScope())
        {
            var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();

            await InsertCredentialAsync(connection, "local-user", "ENC:test-token");
            await InsertCardAsync(
                connection,
                "card-stale",
                board.Id,
                inboxLane.Id,
                "Old stale card",
                1024,
                archivedAt: null,
                sourceType: "github-issue",
                sourceKey: "github:acme/rocket#99",
                metadata: "{\"number\":99,\"state\":\"open\"}");
        }

        var syncResponse = await client.PostAsync($"/api/boards/{board.Id}/sync", content: null);
        syncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var syncResult = await syncResponse.Content.ReadFromJsonAsync<BoardSyncDto>(JsonSerializerOptions.Web);
        syncResult.ShouldNotBeNull();
        syncResult.SourcesProcessed.ShouldBe(1);
        syncResult.IssuesFetched.ShouldBe(2);
        syncResult.CardsCreated.ShouldBe(2);
        syncResult.CardsUpdated.ShouldBe(0);
        syncResult.CardsMarkedStale.ShouldBe(1);
        syncResult.SyncedAt.ShouldNotBeNullOrWhiteSpace();

        var cardsAfterSync = await GetBoardCardsAsync(client, board.Id);
        cardsAfterSync.Length.ShouldBe(3);
        var syncedCardOne = cardsAfterSync.Single(item => item.SourceKey == "github:acme/rocket#1");
        syncedCardOne.Title.ShouldBe("Issue one");
        syncedCardOne.LaneId.ShouldBe(inboxLane.Id);
        var syncedCardOneMetadata = JsonNode.Parse(syncedCardOne.Metadata!).ShouldBeOfType<JsonObject>();
        syncedCardOneMetadata["number"]!.GetValue<int>().ShouldBe(1);

        var syncedCardTwo = cardsAfterSync.Single(item => item.SourceKey == "github:acme/rocket#2");
        syncedCardTwo.Title.ShouldBe("Issue two");

        var staleCard = cardsAfterSync.Single(item => item.SourceKey == "github:acme/rocket#99");
        var staleMetadata = JsonNode.Parse(staleCard.Metadata!).ShouldBeOfType<JsonObject>();
        staleMetadata["stale"]!.GetValue<bool>().ShouldBeTrue();

        var sourcesAfterSyncResponse = await client.GetAsync($"/api/boards/{board.Id}/sources");
        sourcesAfterSyncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sourcesAfterSync = await sourcesAfterSyncResponse.Content.ReadFromJsonAsync<BoardSourceDto[]>(JsonSerializerOptions.Web);
        sourcesAfterSync.ShouldNotBeNull();
        sourcesAfterSync.Single().LastSyncAt.ShouldNotBeNullOrWhiteSpace();

        var deleteSourceResponse = await client.DeleteAsync($"/api/boards/{board.Id}/sources/{createdSource.Id}");
        deleteSourceResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var remainingSourcesResponse = await client.GetAsync($"/api/boards/{board.Id}/sources");
        remainingSourcesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var remainingSources = await remainingSourcesResponse.Content.ReadFromJsonAsync<BoardSourceDto[]>(JsonSerializerOptions.Web);
        remainingSources.ShouldNotBeNull();
        remainingSources.ShouldBeEmpty();

        var cardsAfterSourceDelete = await GetBoardCardsAsync(client, board.Id);
        cardsAfterSourceDelete.Length.ShouldBe(3);
        cardsAfterSourceDelete.Select(item => item.Id).ShouldBe(cardsAfterSync.Select(item => item.Id));
        cardsAfterSourceDelete.Single(item => item.SourceKey == "github:acme/rocket#1").Title.ShouldBe("Issue one");
        cardsAfterSourceDelete.Single(item => item.SourceKey == "github:acme/rocket#2").Title.ShouldBe("Issue two");
        var staleCardAfterSourceDelete = cardsAfterSourceDelete.Single(item => item.SourceKey == "github:acme/rocket#99");
        JsonNode.Parse(staleCardAfterSourceDelete.Metadata!).ShouldBeOfType<JsonObject>()["stale"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task SourceEndpoints_ResyncAfterManualMove_PreservesLaneAndRefreshesMetadata()
    {
        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<GitHubApiProxy>();
                services.RemoveAll<IIntegrationStore>();
                services.RemoveAll<IPluginStateStore>();
                services.RemoveAll<ICredentialProtector>();
                services.AddSingleton<IIntegrationStore, InMemoryIntegrationStore>();
                services.AddSingleton<IPluginStateStore, PluginStateStore>();
                services.AddSingleton<ICredentialProtector, FakeCredentialProtector>();
                services.AddSingleton<GitHubApiProxy>(_ =>
                {
                    var handler = new FakeGitHubIssuesHandler(
                        CreateIssuesPayload(CreateIssue(1, "Issue one", "open", CreateLabels("bug"), "octocat", "2026-04-24T12:00:00Z")),
                        CreateIssuesPayload(CreateIssue(1, "Issue one updated", "closed", CreateLabels("bug", "urgent"), "hubot", "2026-04-24T13:00:00Z")));
                    return new GitHubApiProxy(new TestHttpClientFactory(handler));
                });
            });
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Smoke Test Board");
        var inboxLane = await CreateLaneAsync(client, board.Id, "Inbox");
        var movedLane = await CreateLaneAsync(client, board.Id, "Doing");

        var setInboxResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/lanes/{inboxLane.Id}",
            new { isInbox = true });
        setInboxResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createSourceResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/sources",
            new { providerType = "github", config = "{\"repository\":\"acme/rocket\"}" });
        createSourceResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using (var scope = factory.Services.CreateScope())
        {
            var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            await InsertCredentialAsync(connection, "local-user", "ENC:test-token");
        }

        var firstSyncResponse = await client.PostAsync($"/api/boards/{board.Id}/sync", content: null);
        firstSyncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstSync = await firstSyncResponse.Content.ReadFromJsonAsync<BoardSyncDto>(JsonSerializerOptions.Web);
        firstSync.ShouldNotBeNull();
        firstSync.CardsCreated.ShouldBe(1);
        firstSync.CardsUpdated.ShouldBe(0);

        var cardsAfterFirstSync = await GetBoardCardsAsync(client, board.Id);
        cardsAfterFirstSync.Length.ShouldBe(1);
        var syncedCard = cardsAfterFirstSync.Single();
        syncedCard.SourceKey.ShouldBe("github:acme/rocket#1");
        syncedCard.Title.ShouldBe("Issue one");
        syncedCard.LaneId.ShouldBe(inboxLane.Id);
        syncedCard.Position.ShouldBe(1_024);
        var firstMetadata = JsonNode.Parse(syncedCard.Metadata!).ShouldBeOfType<JsonObject>();
        firstMetadata["state"]!.GetValue<string>().ShouldBe("open");
        firstMetadata["assignee"]!.GetValue<string>().ShouldBe("octocat");
        firstMetadata["labels"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray().ShouldBe(["bug"]);

        var moveCardResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{syncedCard.Id}/move",
            new { laneId = movedLane.Id, position = 0 });
        moveCardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var movedCard = await moveCardResponse.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web);
        movedCard.ShouldNotBeNull();
        movedCard.LaneId.ShouldBe(movedLane.Id);
        movedCard.Position.ShouldBe(1_024);

        var secondSyncResponse = await client.PostAsync($"/api/boards/{board.Id}/sync", content: null);
        secondSyncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondSync = await secondSyncResponse.Content.ReadFromJsonAsync<BoardSyncDto>(JsonSerializerOptions.Web);
        secondSync.ShouldNotBeNull();
        secondSync.CardsCreated.ShouldBe(0);
        secondSync.CardsUpdated.ShouldBe(1);

        var cardsAfterSecondSync = await GetBoardCardsAsync(client, board.Id);
        cardsAfterSecondSync.Length.ShouldBe(1);
        var resyncedCard = cardsAfterSecondSync.Single();
        resyncedCard.Id.ShouldBe(syncedCard.Id);
        resyncedCard.LaneId.ShouldBe(movedLane.Id);
        resyncedCard.Position.ShouldBe(1_024);
        resyncedCard.Title.ShouldBe("Issue one updated");

        var secondMetadata = JsonNode.Parse(resyncedCard.Metadata!).ShouldBeOfType<JsonObject>();
        secondMetadata["state"]!.GetValue<string>().ShouldBe("closed");
        secondMetadata["assignee"]!.GetValue<string>().ShouldBe("hubot");
        secondMetadata["updated_at"]!.GetValue<string>().ShouldBe("2026-04-24T13:00:00Z");
        secondMetadata["labels"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray().ShouldBe(["bug", "urgent"]);
    }

    [Fact]
    public async Task SourceEndpoints_InvalidOperations_ReturnExpectedErrors()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var board = await CreateBoardAsync(client, "Board");

        var invalidCreateResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/sources",
            new { providerType = "github", config = "[]" });
        invalidCreateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var missingBoardSourcesResponse = await client.GetAsync("/api/boards/missing-board/sources");
        missingBoardSourcesResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var createSourceResponse = await client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/sources",
            new { providerType = "github", config = "{\"repository\":\"acme/rocket\"}" });
        createSourceResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var source = (await createSourceResponse.Content.ReadFromJsonAsync<BoardSourceDto>(JsonSerializerOptions.Web)).ShouldNotBeNull();

        var invalidUpdateResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/sources/{source.Id}",
            new { providerType = "   " });
        invalidUpdateResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var missingSourceUpdateResponse = await PatchAsJsonAsync(
            client,
            $"/api/boards/{board.Id}/sources/missing-source",
            new { config = "{\"repository\":\"acme/rocket\"}" });
        missingSourceUpdateResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var syncWithoutInboxResponse = await client.PostAsync($"/api/boards/{board.Id}/sync", content: null);
        syncWithoutInboxResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BoardEndpoints_EnforceOwnership()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var csrfToken = await GetCsrfTokenAsync(client);

        using var scope = factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        await InsertBoardAsync(connection, "board-mine", "test-user", "Mine");
        await InsertBoardAsync(connection, "board-other", "other-user", "Theirs");
        await InsertLaneAsync(connection, "lane-other", "board-other", "Other Lane", 1024, isInbox: false);
        await InsertCardAsync(connection, "card-other", "board-other", "lane-other", "Other Card", 1024, archivedAt: null);

        var listBoardsResponse = await client.GetAsync("/api/boards");
        listBoardsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var boards = await listBoardsResponse.Content.ReadFromJsonAsync<BoardDto[]>(JsonSerializerOptions.Web);
        boards.ShouldNotBeNull();
        boards.ShouldContain(item => item.Id == "board-mine");
        boards.ShouldNotContain(item => item.Id == "board-other");

        var getOtherBoardLanesResponse = await client.GetAsync("/api/boards/board-other/lanes");
        getOtherBoardLanesResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var updateOtherBoardRequest = CreateJsonRequest(HttpMethod.Patch, "/api/boards/board-other", new { name = "Hacked" }, csrfToken);
        var updateOtherBoardResponse = await client.SendAsync(updateOtherBoardRequest);
        updateOtherBoardResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var deleteOtherCardRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/boards/board-other/cards/card-other");
        deleteOtherCardRequest.Headers.Add("X-CSRF-Token", csrfToken);
        var deleteOtherCardResponse = await client.SendAsync(deleteOtherCardRequest);
        deleteOtherCardResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<BoardDto> CreateBoardAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/boards", new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BoardDto>(JsonSerializerOptions.Web)).ShouldNotBeNull();
    }

    private static async Task<BoardLaneDto> CreateLaneAsync(HttpClient client, string boardId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/lanes", new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BoardLaneDto>(JsonSerializerOptions.Web)).ShouldNotBeNull();
    }

    private static async Task<BoardCardDto> CreateCardAsync(HttpClient client, string boardId, string laneId, string title)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", new { laneId, title });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BoardCardDto>(JsonSerializerOptions.Web)).ShouldNotBeNull();
    }

    private static async Task<BoardLaneDto[]> GetBoardLanesAsync(HttpClient client, string boardId)
    {
        var response = await client.GetAsync($"/api/boards/{boardId}/lanes");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BoardLaneDto[]>(JsonSerializerOptions.Web)).ShouldNotBeNull();
    }

    private static async Task<BoardCardDto[]> GetBoardCardsAsync(HttpClient client, string boardId)
    {
        var response = await client.GetAsync($"/api/boards/{boardId}/cards");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BoardCardDto[]>(JsonSerializerOptions.Web)).ShouldNotBeNull();
    }

    private static Task<HttpResponseMessage> PatchAsJsonAsync<T>(HttpClient client, string requestUri, T payload)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, requestUri)
        {
            Content = JsonContent.Create(payload)
        });

    private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string uri, T payload, string csrfToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Add("X-CSRF-Token", csrfToken);
        return request;
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/user/me");
        response.EnsureSuccessStatusCode();

        var csrfToken = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();
        return csrfToken;
    }

    private static string? ExtractCookieValue(IEnumerable<string> setCookies, string cookieName)
    {
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';');
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header.Substring(cookieName.Length + 1);
        }

        return null;
    }

    private static Task<int> InsertBoardAsync(IDbConnection connection, string id, string userId, string name)
        => connection.ExecuteAsync(
            "INSERT INTO boards (id, user_id, name, created_at, updated_at) VALUES (@Id, @UserId, @Name, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = id,
                UserId = userId,
                Name = name,
                CreatedAt = "2026-04-01T00:00:00.0000000Z",
                UpdatedAt = "2026-04-01T00:00:00.0000000Z"
            });

    private static Task<int> InsertLaneAsync(IDbConnection connection, string id, string boardId, string name, int position, bool isInbox)
        => connection.ExecuteAsync(
            "INSERT INTO board_lanes (id, board_id, name, position, is_inbox, created_at, updated_at) VALUES (@Id, @BoardId, @Name, @Position, @IsInbox, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = id,
                BoardId = boardId,
                Name = name,
                Position = position,
                IsInbox = isInbox,
                CreatedAt = "2026-04-01T00:00:00.0000000Z",
                UpdatedAt = "2026-04-01T00:00:00.0000000Z"
            });

    private static Task<int> InsertCardAsync(IDbConnection connection, string id, string boardId, string laneId, string title, int position, string? archivedAt)
        => connection.ExecuteAsync(
            "INSERT INTO board_cards (id, board_id, lane_id, title, source_type, source_key, metadata, position, archived_at, created_at, updated_at) VALUES (@Id, @BoardId, @LaneId, @Title, @SourceType, @SourceKey, @Metadata, @Position, @ArchivedAt, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = id,
                BoardId = boardId,
                LaneId = laneId,
                Title = title,
                SourceType = (string?)null,
                SourceKey = (string?)null,
                Metadata = (string?)null,
                Position = position,
                ArchivedAt = archivedAt,
                CreatedAt = "2026-04-01T00:00:00.0000000Z",
                UpdatedAt = "2026-04-01T00:00:00.0000000Z"
            });

    private static Task<int> InsertCardAsync(
        IDbConnection connection,
        string id,
        string boardId,
        string laneId,
        string title,
        int position,
        string? archivedAt,
        string? sourceType,
        string? sourceKey,
        string? metadata)
        => connection.ExecuteAsync(
            "INSERT INTO board_cards (id, board_id, lane_id, title, source_type, source_key, metadata, position, archived_at, created_at, updated_at) VALUES (@Id, @BoardId, @LaneId, @Title, @SourceType, @SourceKey, @Metadata, @Position, @ArchivedAt, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = id,
                BoardId = boardId,
                LaneId = laneId,
                Title = title,
                SourceType = sourceType,
                SourceKey = sourceKey,
                Metadata = metadata,
                Position = position,
                ArchivedAt = archivedAt,
                CreatedAt = "2026-04-01T00:00:00.0000000Z",
                UpdatedAt = "2026-04-01T00:00:00.0000000Z"
            });

    private static Task<int> InsertCredentialAsync(IDbConnection connection, string userId, string encryptedValue)
        => connection.ExecuteAsync(
            "INSERT INTO user_credentials (id, user_id, namespace, kind, label, encrypted_value, display_hint, metadata, created_at, updated_at) VALUES (@Id, @UserId, @Namespace, @Kind, @Label, @EncryptedValue, @DisplayHint, @Metadata, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Namespace = "github",
                Kind = "oauth-access-token",
                Label = "GitHub",
                EncryptedValue = encryptedValue,
                DisplayHint = "...oken",
                Metadata = (string?)null,
                CreatedAt = "2026-04-01T00:00:00.0000000Z",
                UpdatedAt = "2026-04-01T00:00:00.0000000Z"
            });

    private static JsonArray CreateIssuesPayload(params JsonObject[] issues)
        => [.. issues];

    private static JsonObject CreateIssue(int number, string title, string state, JsonArray labels, string? assignee, string updatedAt)
        => new()
        {
            ["number"] = number,
            ["title"] = title,
            ["state"] = state,
            ["html_url"] = $"https://github.com/acme/rocket/issues/{number}",
            ["updated_at"] = updatedAt,
            ["labels"] = labels,
            ["assignee"] = assignee is null ? null : new JsonObject { ["login"] = assignee }
        };

    private static JsonArray CreateLabels(params string[] labels)
        => [.. labels.Select(label => (JsonNode)new JsonObject { ["name"] = label })];
}

internal sealed record BoardDto(string Id, string Name, string CreatedAt, string UpdatedAt);

internal sealed record BoardLaneDto(string Id, string BoardId, string Name, int Position, bool IsInbox, string CreatedAt, string UpdatedAt);

internal sealed record BoardCardDto(
    string Id,
    string BoardId,
    string LaneId,
    string Title,
    string? SourceType,
    string? SourceKey,
    string? Metadata,
    int Position,
    string? ArchivedAt,
    string CreatedAt,
    string UpdatedAt);

internal sealed record BoardSourceDto(
    string Id,
    string BoardId,
    string ProviderType,
    string Config,
    string? LastSyncAt,
    string CreatedAt,
    string UpdatedAt);

internal sealed record BoardSyncDto(
    int SourcesProcessed,
    int IssuesFetched,
    int CardsCreated,
    int CardsUpdated,
    int CardsMarkedStale,
    string SyncedAt);

internal sealed class InMemoryIntegrationStore : IIntegrationStore
{
    private readonly Dictionary<(string PluginId, string UserId), JsonObject> _states = [];

    public Task<JsonObject?> GetConfigAsync(string pluginId, string userId, CancellationToken cancellationToken)
    {
        _states.TryGetValue((pluginId, userId), out var value);
        return Task.FromResult(value?.DeepClone().AsObject());
    }

    public Task SetConfigAsync(string pluginId, string userId, JsonObject config, CancellationToken cancellationToken)
    {
        _states[(pluginId, userId)] = config.DeepClone().AsObject();
        return Task.CompletedTask;
    }

    public Task RemoveConfigAsync(string pluginId, string userId, CancellationToken cancellationToken)
    {
        _states.Remove((pluginId, userId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, JsonObject>> GetAllConfigsAsync(string userId, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonObject> result = _states
            .Where(item => string.Equals(item.Key.UserId, userId, StringComparison.Ordinal))
            .ToDictionary(item => item.Key.PluginId, item => item.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        return Task.FromResult(result);
    }
}

internal sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(handler, disposeHandler: false);
}

internal sealed class FakeGitHubIssuesHandler : HttpMessageHandler
{
    private readonly JsonArray[] _responses;
    private int _requestIndex;

    public FakeGitHubIssuesHandler()
        : this(
            new[]
            {
                new JsonArray
                {
                    new JsonObject
                    {
                        ["number"] = 1,
                        ["title"] = "Issue one",
                        ["state"] = "open",
                        ["html_url"] = "https://github.com/acme/rocket/issues/1",
                        ["updated_at"] = "2026-04-24T12:00:00Z",
                        ["labels"] = new JsonArray(new JsonObject { ["name"] = "bug" }),
                        ["assignee"] = new JsonObject { ["login"] = "octocat" }
                    },
                    new JsonObject
                    {
                        ["number"] = 2,
                        ["title"] = "Issue two",
                        ["state"] = "open",
                        ["html_url"] = "https://github.com/acme/rocket/issues/2",
                        ["updated_at"] = "2026-04-24T12:05:00Z",
                        ["labels"] = new JsonArray(),
                        ["assignee"] = null
                    }
                }
            })
    {
    }

    public FakeGitHubIssuesHandler(params JsonArray[] responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _requestIndex) - 1;
        var body = _responses[Math.Min(index, _responses.Length - 1)];

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}
