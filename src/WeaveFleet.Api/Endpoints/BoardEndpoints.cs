using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WeaveFleet.Api;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class BoardEndpoints
{
    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/boards").WithTags("Boards");

        group.MapGet("/", async (IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var boards = await boardRepository.ListByUserAsync(userContext.UserId);
            return Results.Ok(boards.Select(ToResponse).ToList());
        })
        .Produces<List<BoardResponse>>(200)
        .WithName("GetBoards");

        group.MapPost("/", async (CreateBoardRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var name = NormalizeRequired(req.Name);
            if (name is null)
                return Results.BadRequest(new ErrorResponse("Board name is required."));

            var now = DateTimeOffset.UtcNow.ToString("O");
            var board = new Board
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userContext.UserId,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now
            };

            await boardRepository.InsertAsync(board);
            return Results.Created($"/api/boards/{board.Id}", ToResponse(board));
        })
        .Produces<BoardResponse>(201)
        .Produces(400)
        .WithName("CreateBoard");

        group.MapPatch("/{boardId}", async (string boardId, UpdateBoardRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var name = NormalizeRequired(req.Name);
            if (name is null)
                return Results.BadRequest(new ErrorResponse("Board name is required."));

            board.Name = name;
            board.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");

            await boardRepository.UpdateAsync(board);

            var updatedBoard = await boardRepository.GetByIdAsync(boardId, userContext.UserId) ?? board;
            return Results.Ok(ToResponse(updatedBoard));
        })
        .Produces<BoardResponse>(200)
        .Produces(400)
        .Produces(404)
        .WithName("UpdateBoard");

        group.MapDelete("/{boardId}", async (string boardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var deleted = await boardRepository.DeleteAsync(boardId, userContext.UserId);
            return deleted
                ? Results.NoContent()
                : NotFound("Board not found.");
        })
        .Produces(204)
        .Produces(404)
        .WithName("DeleteBoard");

        group.MapGet("/{boardId}/sources", async (string boardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var sources = await boardRepository.GetSourcesByBoardIdAsync(boardId, userContext.UserId);
            return Results.Ok(sources.Select(ToResponse).ToList());
        })
        .Produces<List<BoardSourceResponse>>(200)
        .Produces(404)
        .WithName("GetBoardSources");

        group.MapPost("/{boardId}/sources", async (string boardId, CreateBoardSourceRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var providerType = NormalizeRequired(req.ProviderType);
            if (providerType is null)
                return Results.BadRequest(new ErrorResponse("Source providerType is required."));

            var configResult = NormalizeJsonObject(req.Config, "Source config is required.");
            if (configResult.IsFailure)
                return Results.BadRequest(new ErrorResponse(configResult.Error.Description));

            var now = DateTimeOffset.UtcNow.ToString("O");
            var source = new BoardSource
            {
                Id = Guid.NewGuid().ToString(),
                BoardId = boardId,
                ProviderType = providerType,
                Config = configResult.Value,
                LastSyncAt = null,
                CreatedAt = now,
                UpdatedAt = now
            };

            await boardRepository.InsertSourceAsync(source);

            var createdSource = await GetSourceByIdAsync(boardRepository, boardId, source.Id, userContext.UserId) ?? source;
            return Results.Created($"/api/boards/{boardId}/sources/{createdSource.Id}", ToResponse(createdSource));
        })
        .Produces<BoardSourceResponse>(201)
        .Produces(400)
        .Produces(404)
        .WithName("CreateBoardSource");

        group.MapPatch("/{boardId}/sources/{sourceId}", async (string boardId, string sourceId, UpdateBoardSourceRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var source = await GetSourceByIdAsync(boardRepository, boardId, sourceId, userContext.UserId);
            if (source is null)
                return NotFound("Source not found.");

            if (req.ProviderType is not null && NormalizeRequired(req.ProviderType) is null)
                return Results.BadRequest(new ErrorResponse("Source providerType is required."));

            Result<string>? configResult = null;
            if (req.Config is not null)
            {
                configResult = NormalizeJsonObject(req.Config, "Source config is required.");
                if (configResult.IsFailure)
                    return Results.BadRequest(new ErrorResponse(configResult.Error.Description));
            }

            var updatedSource = new BoardSource
            {
                Id = source.Id,
                BoardId = source.BoardId,
                ProviderType = req.ProviderType is null ? source.ProviderType : req.ProviderType.Trim(),
                Config = configResult?.Value ?? source.Config,
                LastSyncAt = source.LastSyncAt,
                CreatedAt = source.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            await boardRepository.UpdateSourceAsync(updatedSource);

            var persistedSource = await GetSourceByIdAsync(boardRepository, boardId, sourceId, userContext.UserId) ?? updatedSource;
            return Results.Ok(ToResponse(persistedSource));
        })
        .Produces<BoardSourceResponse>(200)
        .Produces(400)
        .Produces(404)
        .WithName("UpdateBoardSource");

        group.MapDelete("/{boardId}/sources/{sourceId}", async (string boardId, string sourceId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var source = await GetSourceByIdAsync(boardRepository, boardId, sourceId, userContext.UserId);
            if (source is null)
                return NotFound("Source not found.");

            var deleted = await boardRepository.DeleteSourceAsync(boardId, sourceId, userContext.UserId);
            return deleted
                ? Results.NoContent()
                : NotFound("Source not found.");
        })
        .Produces(204)
        .Produces(404)
        .WithName("DeleteBoardSource");

        group.MapPost("/{boardId}/sync", async (string boardId, IBoardSyncService boardSyncService, CancellationToken cancellationToken) =>
        {
            var result = await boardSyncService.SyncAsync(boardId, cancellationToken);
            return result.Match<IResult>(
                value => Results.Ok(ToResponse(value)),
                error => error.ToBoardApiResult());
        })
        .Produces<BoardSyncResponse>(200)
        .Produces(400)
        .Produces(401)
        .Produces(404)
        .WithName("SyncBoard");

        group.MapGet("/{boardId}/lanes", async (string boardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var lanes = await boardRepository.ListLanesAsync(boardId, userContext.UserId);
            return Results.Ok(lanes.Select(ToResponse).ToList());
        })
        .Produces<List<BoardLaneResponse>>(200)
        .Produces(404)
        .WithName("GetBoardLanes");

        group.MapPost("/{boardId}/lanes", async (string boardId, CreateBoardLaneRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var name = NormalizeRequired(req.Name);
            if (name is null)
                return Results.BadRequest(new ErrorResponse("Lane name is required."));

            if (!IsValidPosition(req.Position))
                return Results.BadRequest(new ErrorResponse("Position must be zero or greater."));

            var now = DateTimeOffset.UtcNow.ToString("O");
            var lane = new BoardLane
            {
                Id = Guid.NewGuid().ToString(),
                BoardId = boardId,
                Name = name,
                Position = 0,
                IsInbox = false,
                CreatedAt = now,
                UpdatedAt = now
            };

            await boardRepository.InsertLaneAsync(lane);

            if (req.Position is not null)
                await ReorderLaneAsync(boardRepository, boardId, lane.Id, userContext.UserId, req.Position.Value);

            var createdLane = await boardRepository.GetLaneByIdAsync(boardId, lane.Id, userContext.UserId) ?? lane;
            return Results.Created($"/api/boards/{boardId}/lanes/{createdLane.Id}", ToResponse(createdLane));
        })
        .Produces<BoardLaneResponse>(201)
        .Produces(400)
        .Produces(404)
        .WithName("CreateBoardLane");

        group.MapPatch("/{boardId}/lanes/{laneId}", async (string boardId, string laneId, UpdateBoardLaneRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var lane = await boardRepository.GetLaneByIdAsync(boardId, laneId, userContext.UserId);
            if (lane is null)
                return NotFound("Lane not found.");

            if (req.Name is not null && NormalizeRequired(req.Name) is null)
                return Results.BadRequest(new ErrorResponse("Lane name is required."));

            if (!IsValidPosition(req.Position))
                return Results.BadRequest(new ErrorResponse("Position must be zero or greater."));

            var updatedLane = new BoardLane
            {
                Id = lane.Id,
                BoardId = lane.BoardId,
                Name = req.Name is null ? lane.Name : req.Name.Trim(),
                Position = lane.Position,
                IsInbox = req.IsInbox ?? lane.IsInbox,
                CreatedAt = lane.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            await boardRepository.UpdateLaneAsync(updatedLane);

            if (req.Position is not null)
                await ReorderLaneAsync(boardRepository, boardId, laneId, userContext.UserId, req.Position.Value);

            var persistedLane = await boardRepository.GetLaneByIdAsync(boardId, laneId, userContext.UserId) ?? updatedLane;
            return Results.Ok(ToResponse(persistedLane));
        })
        .Produces<BoardLaneResponse>(200)
        .Produces(400)
        .Produces(404)
        .WithName("UpdateBoardLane");

        group.MapDelete("/{boardId}/lanes/{laneId}", async (string boardId, string laneId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var lane = await boardRepository.GetLaneByIdAsync(boardId, laneId, userContext.UserId);
            if (lane is null)
                return NotFound("Lane not found.");

            var cards = await boardRepository.ListCardsAsync(boardId, userContext.UserId);
            if (cards.Any(card => string.Equals(card.LaneId, laneId, StringComparison.Ordinal)))
                return Results.Conflict(new ErrorResponse("Cannot delete a lane that still contains cards."));

            var deleted = await boardRepository.DeleteLaneAsync(boardId, laneId, userContext.UserId);
            return deleted
                ? Results.NoContent()
                : NotFound("Lane not found.");
        })
        .Produces(204)
        .Produces(404)
        .Produces(409)
        .WithName("DeleteBoardLane");

        group.MapPatch("/{boardId}/lanes/reorder", async (string boardId, ReorderBoardLanesRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            await boardRepository.ReorderLanesAsync(boardId, userContext.UserId, req.LaneIds);
            return Results.NoContent();
        })
        .Produces(204)
        .Produces(404)
        .WithName("ReorderBoardLanes");

        group.MapGet("/{boardId}/cards", async (string boardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var cards = await boardRepository.ListCardsAsync(boardId, userContext.UserId);
            return Results.Ok(cards.Select(ToResponse).ToList());
        })
        .Produces<List<BoardCardResponse>>(200)
        .Produces(404)
        .WithName("GetBoardCards");

        group.MapPost("/{boardId}/cards", async (string boardId, CreateBoardCardRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId);
            if (board is null)
                return NotFound("Board not found.");

            var lane = await boardRepository.GetLaneByIdAsync(boardId, req.LaneId, userContext.UserId);
            if (lane is null)
                return NotFound("Lane not found.");

            var title = NormalizeRequired(req.Title);
            if (title is null)
                return Results.BadRequest(new ErrorResponse("Card title is required."));

            if (!IsValidPosition(req.Position))
                return Results.BadRequest(new ErrorResponse("Position must be zero or greater."));

            var now = DateTimeOffset.UtcNow.ToString("O");
            var card = new BoardCard
            {
                Id = Guid.NewGuid().ToString(),
                BoardId = boardId,
                LaneId = req.LaneId,
                Title = title,
                Position = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            await boardRepository.InsertCardAsync(card);

            BoardCard? createdCard = await boardRepository.GetCardByIdAsync(boardId, card.Id, userContext.UserId);
            if (req.Position is not null)
                createdCard = await boardRepository.MoveCardAsync(boardId, card.Id, req.LaneId, req.Position.Value, userContext.UserId);

            return Results.Created($"/api/boards/{boardId}/cards/{card.Id}", ToResponse(createdCard ?? card));
        })
        .Produces<BoardCardResponse>(201)
        .Produces(400)
        .Produces(404)
        .WithName("CreateBoardCard");

        group.MapPatch("/{boardId}/cards/{cardId}", async (string boardId, string cardId, UpdateBoardCardRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var card = await boardRepository.GetCardByIdAsync(boardId, cardId, userContext.UserId);
            if (card is null)
                return NotFound("Card not found.");

            if (req.Title is not null && NormalizeRequired(req.Title) is null)
                return Results.BadRequest(new ErrorResponse("Card title is required."));

            if (!IsValidPosition(req.Position))
                return Results.BadRequest(new ErrorResponse("Position must be zero or greater."));

            var targetLaneId = req.LaneId ?? card.LaneId;
            if (!string.Equals(targetLaneId, card.LaneId, StringComparison.Ordinal) || req.Position is not null)
            {
                if (card.ArchivedAt is not null)
                    return Results.Conflict(new ErrorResponse("Archived cards cannot be moved or reordered."));

                var lane = await boardRepository.GetLaneByIdAsync(boardId, targetLaneId, userContext.UserId);
                if (lane is null)
                    return NotFound("Lane not found.");

                var targetPosition = req.Position ?? await GetAppendPositionAsync(boardRepository, boardId, targetLaneId, cardId, userContext.UserId);
                var movedCard = string.Equals(targetLaneId, card.LaneId, StringComparison.Ordinal)
                    ? await boardRepository.ReorderCardAsync(boardId, cardId, targetPosition, userContext.UserId)
                    : await boardRepository.MoveCardAsync(boardId, cardId, targetLaneId, targetPosition, userContext.UserId);

                if (movedCard is null)
                    return NotFound("Card not found.");

                card = movedCard;
            }

            if (req.Title is not null)
            {
                var updatedCard = new BoardCard
                {
                    Id = card.Id,
                    BoardId = card.BoardId,
                    LaneId = card.LaneId,
                    Title = req.Title.Trim(),
                    SourceType = card.SourceType,
                    SourceKey = card.SourceKey,
                    Metadata = card.Metadata,
                    Position = card.Position,
                    ArchivedAt = card.ArchivedAt,
                    CreatedAt = card.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                };

                await boardRepository.UpdateCardAsync(updatedCard);
                card = await boardRepository.GetCardByIdAsync(boardId, cardId, userContext.UserId) ?? updatedCard;
            }

            return Results.Ok(ToResponse(card));
        })
        .Produces<BoardCardResponse>(200)
        .Produces(400)
        .Produces(404)
        .Produces(409)
        .WithName("UpdateBoardCard");

        group.MapDelete("/{boardId}/cards/{cardId}", async (string boardId, string cardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var card = await boardRepository.GetCardByIdAsync(boardId, cardId, userContext.UserId);
            if (card is null)
                return NotFound("Card not found.");

            var deleted = await boardRepository.DeleteCardAsync(boardId, cardId, userContext.UserId);
            return deleted
                ? Results.NoContent()
                : NotFound("Card not found.");
        })
        .Produces(204)
        .Produces(404)
        .WithName("DeleteBoardCard");

        group.MapPost("/{boardId}/cards/{cardId}/archive", async (string boardId, string cardId, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var card = await boardRepository.GetCardByIdAsync(boardId, cardId, userContext.UserId);
            if (card is null)
                return NotFound("Card not found.");

            if (card.ArchivedAt is not null)
                return Results.Ok(ToResponse(card));

            var archivedCard = await boardRepository.ArchiveCardAsync(boardId, cardId, userContext.UserId, DateTimeOffset.UtcNow.ToString("O"));
            return archivedCard is null
                ? NotFound("Card not found.")
                : Results.Ok(ToResponse(archivedCard));
        })
        .Produces<BoardCardResponse>(200)
        .Produces(404)
        .WithName("ArchiveBoardCard");

        group.MapPost("/{boardId}/cards/{cardId}/move", async (string boardId, string cardId, MoveBoardCardRequest req, IBoardRepository boardRepository, IUserContext userContext) =>
        {
            var card = await boardRepository.GetCardByIdAsync(boardId, cardId, userContext.UserId);
            if (card is null)
                return NotFound("Card not found.");

            if (card.ArchivedAt is not null)
                return Results.Conflict(new ErrorResponse("Archived cards cannot be moved."));

            if (req.Position < 0)
                return Results.BadRequest(new ErrorResponse("Position must be zero or greater."));

            var lane = await boardRepository.GetLaneByIdAsync(boardId, req.LaneId, userContext.UserId);
            if (lane is null)
                return NotFound("Lane not found.");

            var movedCard = await boardRepository.MoveCardAsync(boardId, cardId, req.LaneId, req.Position, userContext.UserId);
            return movedCard is null
                ? NotFound("Card not found.")
                : Results.Ok(ToResponse(movedCard));
        })
        .Produces<BoardCardResponse>(200)
        .Produces(400)
        .Produces(404)
        .Produces(409)
        .WithName("MoveBoardCard");

        return app;
    }

    private static BoardResponse ToResponse(Board board) =>
        new(board.Id, board.Name, board.CreatedAt, board.UpdatedAt);

    private static BoardLaneResponse ToResponse(BoardLane lane) =>
        new(lane.Id, lane.BoardId, lane.Name, lane.Position, lane.IsInbox, lane.CreatedAt, lane.UpdatedAt);

    private static BoardSourceResponse ToResponse(BoardSource source) =>
        new(source.Id, source.BoardId, source.ProviderType, source.Config, source.LastSyncAt, source.CreatedAt, source.UpdatedAt);

    private static BoardCardResponse ToResponse(BoardCard card) =>
        new(card.Id, card.BoardId, card.LaneId, card.Title, card.SourceType, card.SourceKey, card.Metadata, card.Position, card.ArchivedAt, card.CreatedAt, card.UpdatedAt);

    private static BoardSyncResponse ToResponse(BoardSyncResult result) =>
        new(result.SourcesProcessed, result.IssuesFetched, result.CardsCreated, result.CardsUpdated, result.CardsMarkedStale, result.SyncedAt);

    private static IResult NotFound(string message) => Results.NotFound(new ErrorResponse(message));

    private static string? NormalizeRequired(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result<string> NormalizeJsonObject(string? value, string requiredMessage)
    {
        var normalized = NormalizeRequired(value);
        if (normalized is null)
            return FleetError.ValidationError(nameof(BoardSource.Config), requiredMessage);

        try
        {
            if (JsonNode.Parse(normalized) is not JsonObject)
            {
                return FleetError.ValidationError(
                    nameof(BoardSource.Config),
                    "Source config must be a JSON object.");
            }

            return Result.Success(normalized);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(nameof(BoardSource.Config), $"Source config is invalid JSON: {ex.Message}");
        }
    }

    private static bool IsValidPosition(int? position) => position is null || position.Value >= 0;

    private static async Task<BoardSource?> GetSourceByIdAsync(IBoardRepository boardRepository, string boardId, string sourceId, string userId)
        => (await boardRepository.GetSourcesByBoardIdAsync(boardId, userId))
            .FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal));

    private static async Task ReorderLaneAsync(IBoardRepository boardRepository, string boardId, string laneId, string userId, int position)
    {
        var laneIds = (await boardRepository.ListLanesAsync(boardId, userId))
            .Select(lane => lane.Id)
            .Where(id => !string.Equals(id, laneId, StringComparison.Ordinal))
            .ToList();

        laneIds.Insert(Math.Clamp(position, 0, laneIds.Count), laneId);
        await boardRepository.ReorderLanesAsync(boardId, userId, laneIds);
    }

    private static async Task<int> GetAppendPositionAsync(IBoardRepository boardRepository, string boardId, string laneId, string cardId, string userId)
    {
        var cards = await boardRepository.ListCardsAsync(boardId, userId);
        return cards.Count(card =>
            string.Equals(card.LaneId, laneId, StringComparison.Ordinal)
            && card.ArchivedAt is null
            && !string.Equals(card.Id, cardId, StringComparison.Ordinal));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBoardRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateBoardRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBoardSourceRequest(string? ProviderType, string? Config);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateBoardSourceRequest(string? ProviderType, string? Config);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBoardLaneRequest(string? Name, int? Position);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateBoardLaneRequest(string? Name, int? Position, bool? IsInbox);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReorderBoardLanesRequest(IReadOnlyList<string> LaneIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBoardCardRequest(string LaneId, string? Title, int? Position);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateBoardCardRequest(string? Title, string? LaneId, int? Position);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MoveBoardCardRequest(string LaneId, int Position);

internal sealed record BoardResponse(string Id, string Name, string CreatedAt, string UpdatedAt);

internal sealed record BoardLaneResponse(string Id, string BoardId, string Name, int Position, bool IsInbox, string CreatedAt, string UpdatedAt);

internal sealed record BoardSourceResponse(
    string Id,
    string BoardId,
    string ProviderType,
    string Config,
    string? LastSyncAt,
    string CreatedAt,
    string UpdatedAt);

internal sealed record BoardCardResponse(
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

internal sealed record BoardSyncResponse(
    int SourcesProcessed,
    int IssuesFetched,
    int CardsCreated,
    int CardsUpdated,
    int CardsMarkedStale,
    string SyncedAt);

file static class BoardFleetErrorExtensions
{
    public static IResult ToBoardApiResult(this FleetError error) =>
        error.Code switch
        {
            var code when code.EndsWith(".NotFound", StringComparison.Ordinal) || code == "General.NotFound"
                => Results.NotFound(new ErrorResponse(error.Description)),
            "General.Unauthorized"
                => Results.Unauthorized(),
            "General.Conflict"
                => Results.Conflict(new ErrorResponse(error.Description)),
            var code when code.StartsWith("Validation.", StringComparison.Ordinal)
                => Results.BadRequest(new ErrorResponse(error.Description)),
            _ => Results.Problem(error.Description)
        };
}
#pragma warning restore IL2026
