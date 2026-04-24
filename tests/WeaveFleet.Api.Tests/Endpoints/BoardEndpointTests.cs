using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

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
