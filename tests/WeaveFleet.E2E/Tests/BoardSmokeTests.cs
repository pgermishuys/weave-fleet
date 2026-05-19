using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Shouldly;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

[Trait("Category", "E2E")]
[Trait("Lane", "Smoke")]
public sealed class BoardSmokeTests : E2ETestBase, IClassFixture<PlaywrightFixture>
{
    private readonly FleetWebApplicationFactory _factory;
    private readonly PlaywrightFixture _playwright;
    private readonly string _databasePath;
    private readonly string _analyticsDatabasePath;
    private bool _factoryDisposed;

    public BoardSmokeTests(PlaywrightFixture playwright)
        : this(CreateDatabasePath("fleet-board-smoke"), CreateDatabasePath("fleet-board-analytics-smoke"), playwright)
    {
    }

    private BoardSmokeTests(string databasePath, string analyticsDatabasePath, PlaywrightFixture playwright)
        : this(new FleetWebApplicationFactory(databasePath, analyticsDatabasePath), databasePath, analyticsDatabasePath, playwright)
    {
    }

    private BoardSmokeTests(
        FleetWebApplicationFactory factory,
        string databasePath,
        string analyticsDatabasePath,
        PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
        _databasePath = databasePath;
        _analyticsDatabasePath = analyticsDatabasePath;
        _playwright = playwright;
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (!_factoryDisposed)
            {
                await _factory.DisposeAsync();
                _factoryDisposed = true;
            }

            DeleteDatabaseFiles(_databasePath);
            DeleteDatabaseFiles(_analyticsDatabasePath);
        }
    }

    [Fact]
    public async Task BoardLifecycle_PersistsAcrossReloadAndRestart()
    {
        await WithFailureCapture(async () =>
        {
            await EnableBoardFeatureAsync(_factory);

            await Page.AddInitScriptAsync("window.confirm = () => true;");
            await Page.GotoAsync("/board", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            await ExpectHeadingAsync(Page, "Kanban Board");
            await Microsoft.Playwright.Assertions.Expect(Page.GetByText("No lanes yet", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await EnsureManageModeAsync(Page);

            await CreateLaneAsync(Page, "Triage");
            await CreateLaneAsync(Page, "Working");
            await CreateLaneAsync(Page, "Done");
            await CreateLaneAsync(Page, "Temp");

            await ExpectHeadingAsync(Page, "My Board");
            await SetInboxLaneAsync(Page, "Working");

            await RenameBoardAsync(Page, "Smoke Board");
            await RenameLaneAsync(Page, "Done", "Closed");

            await CreateCardAsync(Page, "Triage", "Card Alpha");
            await CreateCardAsync(Page, "Working", "Card Beta");
            await RenameCardAsync(Page, "Working", "Card Beta", "Card Beta Renamed");

            await MoveCardAsync(Page, "Triage", "Card Alpha", "Working", 0);
            await MoveLaneLeftAsync(Page, "Closed");
            await ArchiveCardAsync(Page, "Working", "Card Alpha");
            await DeleteLaneAsync(Page, "Temp");

            await VerifyBoardUiStateAsync(Page);

            await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await VerifyBoardUiStateAsync(Page);

            await _factory.DisposeAsync();
            _factoryDisposed = true;

            await using var restartedFactory = new FleetWebApplicationFactory(_databasePath, _analyticsDatabasePath);
            await restartedFactory.EnsureStartedAsync();
            await EnableBoardFeatureAsync(restartedFactory);

            await using var restartedContext = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = restartedFactory.ServerUrl,
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
            });

            var restartedPage = await restartedContext.NewPageAsync();
            restartedPage.SetDefaultTimeout(5_000);
            restartedPage.SetDefaultNavigationTimeout(5_000);

            await restartedPage.AddInitScriptAsync("window.confirm = () => true;");
            await restartedPage.GotoAsync("/board", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await VerifyBoardUiStateAsync(restartedPage);

            using var client = new HttpClient { BaseAddress = new Uri(restartedFactory.ServerUrl) };
            var boards = await client.GetFromJsonAsync<List<BoardDto>>("/api/boards");
            boards.ShouldNotBeNull();
            boards.Count.ShouldBe(1);
            boards[0].Name.ShouldBe("Smoke Board");

            var lanes = await client.GetFromJsonAsync<List<BoardLaneDto>>($"/api/boards/{boards[0].Id}/lanes");
            lanes.ShouldNotBeNull();
            lanes.Count.ShouldBe(3);
            lanes.Select(lane => lane.Name).ShouldBe(["Triage", "Closed", "Working"]);
            lanes.Single(lane => lane.IsInbox).Name.ShouldBe("Working");

            var cards = await client.GetFromJsonAsync<List<BoardCardDto>>($"/api/boards/{boards[0].Id}/cards");
            cards.ShouldNotBeNull();
            cards.Count.ShouldBe(2);
            cards.Single(card => card.Title == "Card Beta Renamed").ArchivedAt.ShouldBeNull();
            cards.Single(card => card.Title == "Card Alpha").ArchivedAt.ShouldNotBeNull();
            cards.Single(card => card.Title == "Card Alpha").LaneId.ShouldBe(lanes.Single(lane => lane.Name == "Working").Id);
        });
    }

    private static string CreateDatabasePath(string filePrefix)
        => Path.Combine(Path.GetTempPath(), $"{filePrefix}-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string databasePath)
    {
        TryDeleteFile(databasePath);
        TryDeleteFile($"{databasePath}-wal");
        TryDeleteFile($"{databasePath}-shm");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup for temp smoke-test databases.
        }
    }

    private static Task ExpectHeadingAsync(IPage page, string heading)
        => Microsoft.Playwright.Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
        {
            Name = heading,
            Exact = true
        })).ToBeVisibleAsync();

    private static async Task EnableBoardFeatureAsync(FleetWebApplicationFactory factory)
    {
        await using var scope = factory.KestrelServices.CreateAsyncScope();
        var preferences = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        await preferences.SetAsync("features.board.enabled", "true");
    }

    private static ILocator GetLaneColumn(IPage page, string laneName)
        => page.GetByLabel($"{laneName} column", new PageGetByLabelOptions { Exact = true });

    private static ILocator GetCard(IPage page, string laneName, string cardTitle)
        => GetLaneColumn(page, laneName).Locator("article.k-card").Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = cardTitle, Exact = true })
        });

    private static async Task CreateLaneAsync(IPage page, string laneName)
    {
        var createFirstLaneButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create first lane", Exact = true });
        var openButton = await createFirstLaneButton.CountAsync() > 0
            ? createFirstLaneButton
            : page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Add another lane", Exact = true });

        await openButton.ClickAsync();
        await page.Locator(".kanban-lane-creator__input").FillAsync(laneName);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save lane", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetLaneColumn(page, laneName)).ToBeVisibleAsync();
    }

    private static async Task EnsureManageModeAsync(IPage page)
    {
        var editBoardButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit Board", Exact = true });
        if (await editBoardButton.CountAsync() == 0)
            return;

        await editBoardButton.ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(page.Locator(".kanban-header__button--mode")).ToHaveTextAsync("Save");
    }

    private static async Task SetInboxLaneAsync(IPage page, string laneName)
    {
        var lane = GetLaneColumn(page, laneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Make inbox", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(lane.Locator(".kanban-col__inbox-pill")).ToBeVisibleAsync();
    }

    private static async Task RenameBoardAsync(IPage page, string newName)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rename board", Exact = true }).ClickAsync();
        await page.Locator(".kanban-header__rename-input").FillAsync(newName);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save board", Exact = true }).ClickAsync();
        await ExpectHeadingAsync(page, newName);
    }

    private static async Task RenameLaneAsync(IPage page, string laneName, string newLaneName)
    {
        var lane = GetLaneColumn(page, laneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rename", Exact = true }).ClickAsync();
        await lane.Locator(".kanban-col__rename-input").FillAsync(newLaneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetLaneColumn(page, newLaneName)).ToBeVisibleAsync();
    }

    private static async Task CreateCardAsync(IPage page, string laneName, string cardTitle)
    {
        var lane = GetLaneColumn(page, laneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add a card", Exact = true }).ClickAsync();
        await lane.Locator(".kanban-col__composer-input").FillAsync(cardTitle);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add card", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, laneName, cardTitle)).ToBeVisibleAsync();
    }

    private static async Task RenameCardAsync(IPage page, string laneName, string cardTitle, string newCardTitle)
    {
        var lane = GetLaneColumn(page, laneName);
        var card = GetCard(page, laneName, cardTitle);
        await card.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rename", Exact = true }).ClickAsync();

        var renameInput = lane.Locator(".k-card__rename-input");
        await renameInput.FillAsync(newCardTitle);

        var editingCard = renameInput.Locator("xpath=ancestor::article[contains(@class, 'k-card')]");
        await editingCard.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, laneName, newCardTitle)).ToBeVisibleAsync();
    }

    private static async Task MoveCardAsync(IPage page, string sourceLaneName, string cardTitle, string targetLaneName, int targetSlotIndex)
    {
        var card = GetCard(page, sourceLaneName, cardTitle);
        var targetLane = GetLaneColumn(page, targetLaneName);
        var targetSlot = targetLane.Locator(".kanban-col__drop-slot").Nth(targetSlotIndex);

        await card.DragToAsync(targetSlot);
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, targetLaneName, cardTitle)).ToBeVisibleAsync();
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, sourceLaneName, cardTitle)).ToHaveCountAsync(0);
    }

    private static async Task MoveLaneLeftAsync(IPage page, string laneName)
    {
        var lane = GetLaneColumn(page, laneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "←", Exact = true }).ClickAsync();

        await page.WaitForFunctionAsync(
            "expected => Array.from(document.querySelectorAll('.kanban-col__title')).map(node => node.textContent?.trim()).join('|') === expected",
            "Triage|Closed|Working|Temp");
    }

    private static async Task ArchiveCardAsync(IPage page, string laneName, string cardTitle)
    {
        var card = GetCard(page, laneName, cardTitle);
        await card.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Archive", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, laneName, cardTitle)).ToHaveCountAsync(0);
    }

    private static async Task DeleteLaneAsync(IPage page, string laneName)
    {
        var lane = GetLaneColumn(page, laneName);
        await lane.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(GetLaneColumn(page, laneName)).ToHaveCountAsync(0);
    }

    private static async Task VerifyBoardUiStateAsync(IPage page)
    {
        await ExpectHeadingAsync(page, "Smoke Board");
        await Microsoft.Playwright.Assertions.Expect(GetLaneColumn(page, "Working").Locator(".kanban-col__inbox-pill")).ToBeVisibleAsync();
        await Microsoft.Playwright.Assertions.Expect(GetCard(page, "Working", "Card Beta Renamed")).ToBeVisibleAsync();
        await Microsoft.Playwright.Assertions.Expect(page.GetByText("Card Alpha", new PageGetByTextOptions { Exact = true })).ToHaveCountAsync(0);
        await Microsoft.Playwright.Assertions.Expect(GetLaneColumn(page, "Temp")).ToHaveCountAsync(0);

        var laneTitles = await page.Locator(".kanban-col__title").AllTextContentsAsync();
        laneTitles.Select(title => title.Trim()).ShouldBe(["Triage", "Closed", "Working"]);
    }

    private sealed record BoardDto(string Id, string Name);

    private sealed record BoardLaneDto(string Id, string Name, int Position, bool IsInbox);

    private sealed record BoardCardDto(string Id, string LaneId, string Title, int Position, string? ArchivedAt);
}
