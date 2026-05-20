using Microsoft.Playwright;
using Xunit.Sdk;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Abstract base class for opt-in Playwright smoke tests that exercise the real harness runtime.
/// Provides a fresh browser context, page, Fleet server URL, and per-run harness working directory.
/// Captures Playwright traces and screenshots on test failure.
/// </summary>
[Trait("Category", "HarnessSmoke")]
public abstract class HarnessSmokeTestBase
{
    private const string AlwaysSaveTraceEnvironmentVariable = "ALWAYS_SAVE_TRACE";
    private const string SmokeEnvironmentVariable = "FLEET_HARNESS_SMOKE";
    private readonly PlaywrightFixture _playwright;
    private SmokeFleetWebApplicationFactory? _factory;
    private IBrowserContext? _context;
    private bool _testFailed;

    protected HarnessSmokeTestBase(PlaywrightFixture playwright)
    {
        _playwright = playwright;
    }

    /// <summary>The Playwright page for this test (fresh per test via isolated browser context).</summary>
    protected IPage Page { get; private set; } = null!;

    /// <summary>The server base URL for this test (e.g. <c>http://127.0.0.1:54321</c>).</summary>
    protected string ServerUrl => CurrentFactory.ServerUrl;

    /// <summary>The per-run temporary directory smoke tests should use for real harness sessions.</summary>
    protected string WorkingDirectory => CurrentFactory.WorkingDirectory;

    /// <summary>Returns the DI service provider from the running Kestrel host.</summary>
    protected IServiceProvider KestrelServices => CurrentFactory.KestrelServices;

    protected async Task RunWithHarnessSmokeFactoryAsync(HarnessSmokeSpec spec, Func<Task> action)
    {
        SkipUnlessHarnessSmokeEnabled();

        await using var factory = new SmokeFleetWebApplicationFactory(spec);
        _factory = factory;

        try
        {
            // Ensure the Kestrel server is started with real harness registrations preserved.
            await factory.EnsureStartedAsync();
            await InitializeBrowserContextAsync();
            await WithFailureCapture(action);
        }
        finally
        {
            await DisposeBrowserContextAsync();
            _factory = null;
        }
    }

    private async Task InitializeBrowserContextAsync()
    {
        _testFailed = false;

        // Create a fresh isolated browser context per test. Smoke tests use longer timeouts because
        // real harness startup and event responses are slower than TestHarness-backed E2E tests.
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

        Page.SetDefaultTimeout(30_000);
        Page.SetDefaultNavigationTimeout(30_000);
    }

    private async Task DisposeBrowserContextAsync()
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
        _context = null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private SmokeFleetWebApplicationFactory CurrentFactory => _factory
        ?? throw new InvalidOperationException("Harness smoke factory has not been started for this test invocation.");

    private static void SkipUnlessHarnessSmokeEnabled()
    {
        var value = Environment.GetEnvironmentVariable(SmokeEnvironmentVariable);
        if (!string.Equals(value, "1", StringComparison.Ordinal))
        {
            throw SkipException.ForSkip(
                $"Harness smoke tests are opt-in. Set {SmokeEnvironmentVariable}=1 to run them.");
        }
    }

    private static string GetArtifactsDirectory()
    {
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
        var className = GetType().Name;
        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        return $"{className}-{timestamp}";
    }
}
