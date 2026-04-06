using Microsoft.Playwright;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// xUnit <see cref="IAsyncLifetime"/> fixture that installs Playwright browsers once per test run
/// and provides a shared <see cref="IBrowser"/> instance to E2E tests.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    /// <summary>The shared Chromium browser instance. Available after <see cref="InitializeAsync"/>.</summary>
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Install Playwright browsers (no-op if already installed)
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser installation failed with exit code {exitCode}. " +
                "Run 'pwsh playwright.ps1 install chromium' manually.");

        _playwright = await Playwright.CreateAsync();

        var headed = string.Equals(
            Environment.GetEnvironmentVariable("HEADED"), "1",
            StringComparison.OrdinalIgnoreCase);

        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            SlowMo = headed ? 200 : 0 // slow down in headed mode for easier debugging
        });
    }

    /// <summary>
    /// Create a new isolated browser page with a fresh context.
    /// The caller is responsible for disposing the context.
    /// </summary>
    public async Task<(IBrowserContext Context, IPage Page)> NewContextAsync(
        string baseUrl,
        BrowserNewContextOptions? options = null)
    {
        var ctxOptions = options ?? new BrowserNewContextOptions();
        ctxOptions.BaseURL = baseUrl;

        var context = await Browser.NewContextAsync(ctxOptions);
        var page = await context.NewPageAsync();
        return (context, page);
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
