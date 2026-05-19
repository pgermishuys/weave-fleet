using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Application.Services;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Abstract base class for all Playwright E2E tests.
/// Provides a fresh browser context, page, server URL, and TestHarness scenario configuration per test.
/// Captures Playwright traces and screenshots on test failure.
/// </summary>
[Trait("Category", "E2E")]
public abstract class E2ETestBase : IAsyncLifetime
{
    private const string AlwaysSaveTraceEnvironmentVariable = "ALWAYS_SAVE_TRACE";
    private readonly FleetWebApplicationFactory _factory;
    private readonly PlaywrightFixture _playwright;
    private IBrowserContext? _context;
    private bool _testFailed;

    protected E2ETestBase(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
    {
        _factory = factory;
        _playwright = playwright;
    }

    /// <summary>The Playwright page for this test (fresh per test via isolated browser context).</summary>
    protected IPage Page { get; private set; } = null!;

    /// <summary>The server base URL for this test (e.g. "http://127.0.0.1:54321").</summary>
    protected string ServerUrl => _factory.ServerUrl;

    /// <summary>The TestHarness singleton for configuring mock scenarios.</summary>
    protected TestHarness.TestHarness TestHarness => _factory.TestHarness;

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public virtual async Task InitializeAsync()
    {
        // Ensure the Kestrel server is started (idempotent).
        // We use EnsureStartedAsync() instead of accessing factory.Services directly,
        // because the base Services getter casts the server to TestServer which fails with Kestrel.
        await _factory.EnsureStartedAsync();

        await using (var scope = _factory.KestrelServices.CreateAsyncScope())
        {
            var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
            var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var addRootResult = await workspaceRootService.AddRootAsync(tempRoot);
            if (addRootResult.IsFailure &&
                !addRootResult.Error.Description.Contains("already registered", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Failed to register E2E workspace root '{tempRoot}': {addRootResult.Error.Description}");
            }
        }

        // Create a fresh isolated browser context per test
        _context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = ServerUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        });

        // Start tracing for this test
        await _context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = false
        });

        Page = await _context.NewPageAsync();

        // Global timeout: fail fast instead of waiting 30 s (Playwright default)
        Page.SetDefaultTimeout(5_000);
        Page.SetDefaultNavigationTimeout(5_000);
    }

    public virtual async Task DisposeAsync()
    {
        if (_context is null)
            return;

        var saveTrace = _testFailed || ShouldAlwaysSaveTrace();

        if (saveTrace)
        {
            var artifactsDir = GetArtifactsDirectory();
            var testName = GetTestName();
            var tracePath = Path.Combine(artifactsDir, $"{testName}-trace.zip");

            try
            {
                Directory.CreateDirectory(artifactsDir);
                await _context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });

                if (_testFailed)
                {
                    var screenshotPath = Path.Combine(artifactsDir, $"{testName}-screenshot.png");
                    await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
                }
            }
            catch
            {
                // best effort — don't let artifact capture mask the original test failure
            }
        }
        else
        {
            await _context.Tracing.StopAsync(new TracingStopOptions());
        }

        await _context.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Configure the TestHarness scenario for this test.
    /// Call this before any page navigation to ensure the harness is set up.
    /// </summary>
    protected void ConfigureScenario(Action<TestScenarioBuilder> configure)
        => _factory.TestHarnessRuntime.Configure(configure);

    /// <summary>
    /// Mark the test as failed so artifacts are captured on disposal.
    /// Called automatically by <see cref="WithFailureCapture"/>.
    /// </summary>
    protected void MarkFailed() => _testFailed = true;

    /// <summary>
    /// Wraps a test action to automatically capture failure artifacts.
    /// Usage: await WithFailureCapture(async () => { /* test code */ });
    /// </summary>
    protected async Task WithFailureCapture(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            _testFailed = true;
            throw;
        }
    }

    private static string GetArtifactsDirectory()
    {
        // Store artifacts relative to the assembly output directory
        var assemblyDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        return Path.Combine(assemblyDir, "test-results");
    }

    private static bool ShouldAlwaysSaveTrace()
    {
        var value = Environment.GetEnvironmentVariable(AlwaysSaveTraceEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(value))
            return false;

        return string.Equals(value, "1", StringComparison.Ordinal) ||
            bool.TryParse(value, out var enabled) && enabled;
    }

    private string GetTestName()
    {
        // Derive a safe filename from the test class name + timestamp
        var className = GetType().Name;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        return $"{className}-{timestamp}";
    }
}
