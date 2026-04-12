using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Application.Data;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Abstract base class for Playwright E2E tests that require OIDC authentication.
/// Boots Fleet with <c>Auth.Enabled = true</c> against the test Duende IdP via
/// <see cref="AuthFleetWebApplicationFactory"/>, creates an HTTPS-trusting browser context,
/// and provides login/logout helpers.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
public abstract class AuthE2ETestBase : IAsyncLifetime
{
    private readonly AuthFleetWebApplicationFactory _factory;
    private readonly PlaywrightFixture _playwright;
    private IBrowserContext? _context;
    private bool _testFailed;

    protected AuthE2ETestBase(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    {
        _factory = factory;
        _playwright = playwright;
    }

    /// <summary>The Playwright page for this test (fresh per test via isolated browser context).</summary>
    protected IPage Page { get; private set; } = null!;

    /// <summary>The Fleet HTTPS base URL (e.g. <c>https://app.dev.localhost:54321</c>).</summary>
    protected string ServerUrl => _factory.ServerUrl;

    /// <summary>The Duende IdP authority URL (e.g. <c>https://auth.dev.localhost:54399</c>).</summary>
    protected string IdpAuthority => _factory.IdpAuthority;

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public virtual async Task InitializeAsync()
    {
        await _factory.EnsureStartedAsync();

        // Create a fresh browser context with HTTPS error tolerance (self-signed test cert)
        _context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = ServerUrl,
            IgnoreHTTPSErrors = true,
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

        Page.SetDefaultTimeout(10_000);
        Page.SetDefaultNavigationTimeout(10_000);
    }

    public virtual async Task DisposeAsync()
    {
        if (_context is null)
            return;

        if (_testFailed)
        {
            var artifactsDir = GetArtifactsDirectory();
            var testName = GetTestName();
            var tracePath = Path.Combine(artifactsDir, $"{testName}-trace.zip");
            var screenshotPath = Path.Combine(artifactsDir, $"{testName}-screenshot.png");

            try
            {
                Directory.CreateDirectory(artifactsDir);
                await _context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
                await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
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

    // ── Auth Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to a protected URL, follows the OIDC redirect to the IdP login page,
    /// fills credentials, submits, and waits until the browser has returned to Fleet
    /// with an authenticated session cookie set.
    /// </summary>
    /// <param name="returnUrl">The Fleet URL to start the flow from.</param>
    protected Task LoginAsync(string returnUrl)
        => LoginAsync("testuser", "password", returnUrl);

    /// <summary>
    /// Navigates to a protected URL, follows the OIDC redirect to the IdP login page,
    /// fills credentials, submits, and waits until the browser has returned to Fleet
    /// with an authenticated session cookie set.
    /// </summary>
    protected Task LoginAsync(string username, string password)
        => LoginAsync(username, password, "/");

    /// <summary>
    /// Navigates to a protected URL, follows the OIDC redirect to the IdP login page,
    /// fills credentials, submits, and waits until the browser has returned to Fleet
    /// with an authenticated session cookie set.
    /// </summary>
    /// <param name="username">Test user username (e.g. "testuser" or "newuser").</param>
    /// <param name="password">Test user password (e.g. "password").</param>
    /// <param name="returnUrl">The Fleet URL to start the flow from.</param>
    protected async Task LoginAsync(string username, string password, string returnUrl)
    {
        // Navigate to the protected page — Fleet will redirect to /login (branded landing page)
        await Page.GotoAsync(returnUrl);

        // If navigating directly to /auth/login (backend OIDC challenge), we skip the
        // Fleet landing page and go straight to the IdP. Otherwise, click "Sign in" first.
        if (!returnUrl.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            var fleetLoginPage = new FleetLoginPage(Page);
            await fleetLoginPage.WaitForVisibleAsync();
            await fleetLoginPage.ClickSignInAsync();
        }

        // Wait for the IdP login page to appear
        var loginPage = new IdpLoginPage(Page);
        await loginPage.WaitForVisibleAsync();

        // Fill credentials and submit
        await loginPage.FillUsernameAsync(username);
        await loginPage.FillPasswordAsync(password);
        await loginPage.SubmitAsync();

        // Wait for redirect back to Fleet (the auth callback → Fleet SPA)
        await loginPage.WaitForRedirectToFleetAsync(ServerUrl);
    }

    /// <summary>
    /// Asserts that the current browser session is authenticated by calling <c>/api/user/me</c>
    /// and verifying the response is HTTP 200.
    /// </summary>
    protected async Task AssertAuthenticatedAsync()
    {
        var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
        response.Status.ShouldBe(200);
    }

    /// <summary>
    /// Reads the CSRF token from the cookie set by Fleet's antiforgery middleware
    /// and posts to <c>/auth/logout</c> with the token header.
    /// </summary>
    protected async Task LogoutAsync()
    {
        // Fetch the request token cookie emitted on GET requests for authenticated API access.
        var cookies = await _context!.CookiesAsync([ServerUrl]);
        var csrfCookie = cookies.FirstOrDefault(c =>
            c.Name.Equals(".WeaveFleet.CSRF", StringComparison.OrdinalIgnoreCase));

        if (csrfCookie is null || string.IsNullOrWhiteSpace(csrfCookie.Value))
            throw new InvalidOperationException("CSRF request token cookie '.WeaveFleet.CSRF' was not present.");

        // Use the Playwright request context to POST the logout with CSRF header
        var response = await Page.APIRequest.PostAsync(
            $"{ServerUrl}/auth/logout",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["X-CSRF-Token"] = csrfCookie.Value
                }
            });

        // 200 or 302 are both acceptable logout responses
        (response.Status == 200 || response.Status == 302).ShouldBeTrue();
    }

    /// <summary>
    /// Resets onboarding status for a user so subsequent tests see the wizard.
    /// Must be called BEFORE <see cref="LoginAsync(string,string)"/> for tests that
    /// expect the onboarding wizard to appear. This avoids cross-test state pollution
    /// when multiple tests share the same <see cref="AuthFleetWebApplicationFactory"/>.
    /// </summary>
    protected async Task ResetUserOnboardingAsync(string email)
    {
        var connFactory = _factory.KestrelServices.GetRequiredService<IDbConnectionFactory>();
        using var conn = connFactory.CreateConnection();
        conn.Open();
        await conn.ExecuteAsync(
            "UPDATE users SET onboarding_completed_at = NULL WHERE email = @Email",
            new { Email = email });
    }

    /// <summary>
    /// Wraps a test action to automatically capture failure artifacts on failure.
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
        var assemblyDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        return Path.Combine(assemblyDir, "test-results");
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
