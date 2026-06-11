using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WeaveFleet.Api.Tests.Infrastructure;

public sealed class ApiWebApplicationFactory(
    bool authEnabled,
    bool tokenAuthEnabled = false,
    bool useTestAuthentication = false,
    bool testUserIsAdmin = false,
    Action<IServiceCollection>? configureTestServices = null) : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fleet-api-tests-{Guid.NewGuid():N}.db");
    private readonly string _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"fleet-api-tests-analytics-{Guid.NewGuid():N}.db");
    private readonly string _webRootPath = Path.Combine(Path.GetTempPath(), $"fleet-api-tests-wwwroot-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        EnsureWebRootExists();

        builder.UseEnvironment("Testing");
        builder.UseSetting("Fleet:DatabasePath", _dbPath);
        builder.UseSetting("Fleet:AnalyticsDatabasePath", _analyticsDbPath);
        builder.UseSetting("Fleet:AnalyticsEnabled", "false");
        builder.UseSetting("Fleet:Port", "0");
        builder.UseSetting(WebHostDefaults.WebRootKey, _webRootPath);
        builder.UseSetting("Fleet:Auth:Enabled", authEnabled ? "true" : "false");
        builder.UseSetting("Fleet:Auth:TokenAuthEnabled", tokenAuthEnabled ? "true" : "false");
        builder.UseSetting("Fleet:Auth:Authority", "https://example.test");
        builder.UseSetting("Fleet:Auth:ClientId", "test-client");
        builder.UseSetting("Fleet:Auth:ClientSecret", "test-secret");
        builder.UseSetting("Fleet:Auth:AllowedOrigins:0", "http://localhost:3001");

        if (useTestAuthentication)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options =>
                    {
                        options.ClaimsIssuer = testUserIsAdmin ? "admin" : "user";
                    });
            });

            builder.UseSetting("Authentication:DefaultScheme", "Test");
            builder.UseSetting("Authentication:DefaultAuthenticateScheme", "Test");
            builder.UseSetting("Authentication:DefaultChallengeScheme", "Test");
        }
        else if (authEnabled)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Unauthorized")
                    .AddScheme<AuthenticationSchemeOptions, UnauthorizedAuthHandler>("Unauthorized", _ => { });
            });

            builder.UseSetting("Authentication:DefaultScheme", "Unauthorized");
            builder.UseSetting("Authentication:DefaultAuthenticateScheme", "Unauthorized");
            builder.UseSetting("Authentication:DefaultChallengeScheme", "Unauthorized");
        }

        if (configureTestServices is not null)
        {
            builder.ConfigureTestServices(configureTestServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TryDelete(_dbPath);
        TryDelete(_analyticsDbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
        TryDeleteDirectory(_webRootPath);
    }

    private void EnsureWebRootExists()
    {
        Directory.CreateDirectory(_webRootPath);
        File.WriteAllText(Path.Combine(_webRootPath, "index.html"), "<html><body><div id=\"app\">Weave Fleet</div><script src=\"/app.js\"></script></body></html>");
        File.WriteAllText(Path.Combine(_webRootPath, "app.js"), "window.__fleetTest = true;");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "test-user"),
                new Claim("email", "test@example.com"),
                new Claim("name", "Test User")
            };
            if (string.Equals(Options.ClaimsIssuer, "admin", StringComparison.Ordinal))
            {
                claims = [.. claims, new Claim(ClaimTypes.Role, "admin")];
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class UnauthorizedAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
