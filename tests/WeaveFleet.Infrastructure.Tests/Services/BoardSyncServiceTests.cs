using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class BoardSyncServiceTests
{
    [Fact]
    public async Task ShouldCreateNewIssuesInInboxLaneWithGitHubSourceKeys()
    {
        var setup = await CreateTestSetupAsync(
            CreateIssuesPayload(
                CreateIssue(1, "Issue one", "open", CreateLabels("bug"), "octocat", "2026-04-24T12:00:00Z"),
                CreateIssue(2, "Issue two", "open", CreateLabels(), null, "2026-04-24T12:05:00Z")));

        using var _ = setup.Keeper;

        var result = await setup.Service.SyncAsync(setup.Board.Id);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourcesProcessed.ShouldBe(1);
        result.Value.IssuesFetched.ShouldBe(2);
        result.Value.CardsCreated.ShouldBe(2);
        result.Value.CardsUpdated.ShouldBe(0);
        result.Value.CardsMarkedStale.ShouldBe(0);

        var cards = await setup.BoardRepository.ListCardsAsync(setup.Board.Id, setup.UserContext.UserId);
        cards.Count.ShouldBe(2);

        var firstCard = cards.Single(card => card.SourceKey == "github:acme/rocket#1");
        firstCard.Title.ShouldBe("Issue one");
        firstCard.LaneId.ShouldBe(setup.InboxLane.Id);
        firstCard.Position.ShouldBe(1_024);
        firstCard.ArchivedAt.ShouldBeNull();
        var firstMetadata = ParseMetadata(firstCard);
        firstMetadata["stale"].ShouldBeNull();
        firstMetadata["number"]!.GetValue<int>().ShouldBe(1);
        firstMetadata["state"]!.GetValue<string>().ShouldBe("open");
        firstMetadata["html_url"]!.GetValue<string>().ShouldBe("https://github.com/acme/rocket/issues/1");

        var secondCard = cards.Single(card => card.SourceKey == "github:acme/rocket#2");
        secondCard.Title.ShouldBe("Issue two");
        secondCard.LaneId.ShouldBe(setup.InboxLane.Id);
        secondCard.Position.ShouldBe(2_048);
        secondCard.ArchivedAt.ShouldBeNull();
        var secondMetadata = ParseMetadata(secondCard);
        secondMetadata["stale"].ShouldBeNull();
        secondMetadata["number"]!.GetValue<int>().ShouldBe(2);

        var sources = await setup.BoardRepository.GetSourcesByBoardIdAsync(setup.Board.Id, setup.UserContext.UserId);
        sources.Count.ShouldBe(1);
        sources[0].LastSyncAt.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ShouldUpdateExistingIssueOnResyncWithoutChangingLaneOrPosition()
    {
        var setup = await CreateTestSetupAsync(
            CreateIssuesPayload(CreateIssue(1, "Issue one", "open", CreateLabels("bug"), "octocat", "2026-04-24T12:00:00Z")),
            CreateIssuesPayload(CreateIssue(1, "Issue one updated", "closed", CreateLabels("bug", "urgent"), "hubot", "2026-04-24T13:00:00Z")));

        using var _ = setup.Keeper;

        var firstSync = await setup.Service.SyncAsync(setup.Board.Id);
        firstSync.IsSuccess.ShouldBeTrue();
        firstSync.Value.CardsCreated.ShouldBe(1);
        firstSync.Value.CardsUpdated.ShouldBe(0);
        firstSync.Value.CardsMarkedStale.ShouldBe(0);

        var createdCard = (await setup.BoardRepository.ListCardsAsync(setup.Board.Id, setup.UserContext.UserId)).Single();
        var movedCard = new BoardCard
        {
            Id = createdCard.Id,
            BoardId = createdCard.BoardId,
            LaneId = setup.DoingLane.Id,
            Title = createdCard.Title,
            SourceType = createdCard.SourceType,
            SourceKey = createdCard.SourceKey,
            Metadata = createdCard.Metadata,
            Position = 4_096,
            ArchivedAt = createdCard.ArchivedAt,
            CreatedAt = createdCard.CreatedAt,
            UpdatedAt = UtcNow()
        };

        await setup.BoardRepository.UpdateCardAsync(movedCard);

        var secondSync = await setup.Service.SyncAsync(setup.Board.Id);

        secondSync.IsSuccess.ShouldBeTrue();
        secondSync.Value.SourcesProcessed.ShouldBe(1);
        secondSync.Value.IssuesFetched.ShouldBe(1);
        secondSync.Value.CardsCreated.ShouldBe(0);
        secondSync.Value.CardsUpdated.ShouldBe(1);
        secondSync.Value.CardsMarkedStale.ShouldBe(0);

        var updatedCard = (await setup.BoardRepository.ListCardsAsync(setup.Board.Id, setup.UserContext.UserId)).Single();
        updatedCard.Id.ShouldBe(createdCard.Id);
        updatedCard.SourceKey.ShouldBe("github:acme/rocket#1");
        updatedCard.Title.ShouldBe("Issue one updated");
        updatedCard.LaneId.ShouldBe(setup.DoingLane.Id);
        updatedCard.Position.ShouldBe(4_096);
        updatedCard.ArchivedAt.ShouldBeNull();

        var metadata = ParseMetadata(updatedCard);
        metadata["stale"].ShouldBeNull();
        metadata["number"]!.GetValue<int>().ShouldBe(1);
        metadata["state"]!.GetValue<string>().ShouldBe("closed");
        metadata["assignee"]!.GetValue<string>().ShouldBe("hubot");
        metadata["labels"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray().ShouldBe(["bug", "urgent"]);
        metadata["updated_at"]!.GetValue<string>().ShouldBe("2026-04-24T13:00:00Z");
    }

    [Fact]
    public async Task ShouldMarkDisappearedIssuesAsStaleWithoutArchivingThem()
    {
        var setup = await CreateTestSetupAsync(
            CreateIssuesPayload(CreateIssue(2, "Issue two", "open", CreateLabels(), null, "2026-04-24T12:05:00Z")),
            CreateIssuesPayload());

        using var _ = setup.Keeper;

        var firstSync = await setup.Service.SyncAsync(setup.Board.Id);
        firstSync.IsSuccess.ShouldBeTrue();
        firstSync.Value.CardsCreated.ShouldBe(1);

        var createdCard = (await setup.BoardRepository.ListCardsAsync(setup.Board.Id, setup.UserContext.UserId)).Single();
        var movedCard = new BoardCard
        {
            Id = createdCard.Id,
            BoardId = createdCard.BoardId,
            LaneId = setup.DoingLane.Id,
            Title = createdCard.Title,
            SourceType = createdCard.SourceType,
            SourceKey = createdCard.SourceKey,
            Metadata = createdCard.Metadata,
            Position = 3_072,
            ArchivedAt = createdCard.ArchivedAt,
            CreatedAt = createdCard.CreatedAt,
            UpdatedAt = UtcNow()
        };

        await setup.BoardRepository.UpdateCardAsync(movedCard);

        var secondSync = await setup.Service.SyncAsync(setup.Board.Id);

        secondSync.IsSuccess.ShouldBeTrue();
        secondSync.Value.SourcesProcessed.ShouldBe(1);
        secondSync.Value.IssuesFetched.ShouldBe(0);
        secondSync.Value.CardsCreated.ShouldBe(0);
        secondSync.Value.CardsUpdated.ShouldBe(0);
        secondSync.Value.CardsMarkedStale.ShouldBe(1);

        var staleCard = (await setup.BoardRepository.ListCardsAsync(setup.Board.Id, setup.UserContext.UserId)).Single();
        staleCard.Id.ShouldBe(createdCard.Id);
        staleCard.SourceKey.ShouldBe("github:acme/rocket#2");
        staleCard.LaneId.ShouldBe(setup.DoingLane.Id);
        staleCard.Position.ShouldBe(3_072);
        staleCard.ArchivedAt.ShouldBeNull();

        var staleMetadata = ParseMetadata(staleCard);
        staleMetadata["stale"]!.GetValue<bool>().ShouldBeTrue();
        staleMetadata["number"]!.GetValue<int>().ShouldBe(2);
        staleMetadata["state"]!.GetValue<string>().ShouldBe("open");
    }

    private static async Task<TestSetup> CreateTestSetupAsync(params JsonArray[] syncResponses)
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var userContext = new TestUserContext();
        var boardRepository = new BoardRepository(factory, userContext);

        var board = CreateBoard("Board");
        await boardRepository.InsertAsync(board);

        var inboxLane = CreateLane(board.Id, "Inbox", true);
        var doingLane = CreateLane(board.Id, "Doing", false);
        await boardRepository.InsertLaneAsync(inboxLane);
        await boardRepository.InsertLaneAsync(doingLane);

        var source = CreateSource(board.Id, "github", """{"repository":"acme/rocket"}""");
        await boardRepository.InsertSourceAsync(source);

        var credentialRepository = new InMemoryUserCredentialRepository();
        credentialRepository.Seed(new UserCredential
        {
            Id = "cred-1",
            UserId = TestUserContext.DefaultUserId,
            Namespace = "github",
            Kind = "oauth-access-token",
            Label = "GitHub",
            EncryptedValue = "ENC:token",
            DisplayHint = "...oken",
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        });

        var handler = new SequencedGitHubIssuesHandler(syncResponses);
        var httpClientFactory = new TestHttpClientFactory(handler);
        var gitHubService = new GitHubService(
            httpClientFactory,
            new FakePluginStateStore(),
            credentialRepository,
            new FakeCredentialProtector());
        var gitHubApiProxy = new GitHubApiProxy(httpClientFactory);
        var service = new BoardSyncService(boardRepository, gitHubService, gitHubApiProxy, userContext);

        return new TestSetup(keeper, userContext, boardRepository, service, board, inboxLane, doingLane);
    }

    private static Board CreateBoard(string name)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            UserId = TestUserContext.DefaultUserId,
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
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };

    private static JsonArray CreateIssuesPayload(params JsonObject[] issues)
        => [.. issues];

    private static JsonObject CreateIssue(
        int number,
        string title,
        string state,
        JsonArray labels,
        string? assignee,
        string updatedAt)
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

    private static JsonObject ParseMetadata(BoardCard card)
        => JsonNode.Parse(card.Metadata!).ShouldBeOfType<JsonObject>();

    private static string UtcNow()
        => DateTimeOffset.UtcNow.ToString("O");

    private sealed record TestSetup(
        IDisposable Keeper,
        TestUserContext UserContext,
        BoardRepository BoardRepository,
        BoardSyncService Service,
        Board Board,
        BoardLane InboxLane,
        BoardLane DoingLane);

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false);
    }

    private sealed class SequencedGitHubIssuesHandler(params JsonArray[] responses) : HttpMessageHandler
    {
        private readonly JsonArray[] responses = responses;
        private int requestIndex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref requestIndex) - 1;
            var body = responses[Math.Min(index, responses.Length - 1)];

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
