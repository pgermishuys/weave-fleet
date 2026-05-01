using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// API-level tenant isolation tests for all analytics endpoints.
/// Seeds analytics data for two users directly into the analytics DB,
/// then verifies that authenticated requests only return the authenticated user's data.
/// The TestAuthHandler provides <c>sub=test-user</c> for all requests.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class AnalyticsEndpointTenantIsolationTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private AnalyticsWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new AnalyticsWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Wait for application to start (trigger initialization)
        _ = await _client.GetAsync("/api/analytics/summary");

        // Seed analytics data for two users via the IAnalyticsDbConnectionFactory
        using var scope = _factory.Services.CreateScope();
        var analyticsDb = scope.ServiceProvider.GetRequiredService<IAnalyticsDbConnectionFactory>();
        using var conn = analyticsDb.CreateConnection();

        // Seed token_events: test-user has 2 events; other-user has 1 event
        ExecuteNonQuery(conn, """
            INSERT INTO token_events (
                event_id, session_id, project_id, project_name, workspace_directory,
                model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at, user_id)
            VALUES
              ('tu-evt-1','tu-sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               100,200,0,0,0,300, 0.01, 0.009, '2026-03-01T10:00:00+00:00','test-user'),
              ('tu-evt-2','tu-sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               50,100,0,0,0,150, 0.005, 0.004, '2026-03-01T11:00:00+00:00','test-user'),
              ('ou-evt-1','ou-sess-1','proj-b','Beta','/ws2','gpt-4o','openai',
               200,300,0,0,0,500, 0.02, 0.018, '2026-03-01T12:00:00+00:00','other-user')
            """);

        // Seed session_snapshots for two users
        ExecuteNonQuery(conn, """
            INSERT INTO session_snapshots (
                session_id, project_id, project_name, workspace_directory, title, status,
                total_tokens, total_cost, total_estimated_cost,
                message_count, model_ids, created_at, user_id)
            VALUES
              ('tu-sess-1','proj-a','Alpha','/ws','Session A','active',
               450, 0.015, 0.013, 2, '["claude-sonnet"]', '2026-03-01T10:00:00+00:00','test-user'),
              ('ou-sess-1','proj-b','Beta','/ws2','Session B','active',
               500, 0.02, 0.018, 1, '["gpt-4o"]', '2026-03-01T12:00:00+00:00','other-user')
            """);

        // Seed daily_rollups for two users
        ExecuteNonQuery(conn, """
            INSERT INTO daily_rollups (
                date, user_id, project_id, model_id, provider_id,
                total_tokens, total_cost, total_estimated_cost, session_count, message_count)
            VALUES
              ('2026-03-01','test-user','proj-a','claude-sonnet','anthropic', 450, 0.015, 0.013, 1, 2),
              ('2026-03-01','other-user','proj-b','gpt-4o','openai', 500, 0.02, 0.018, 1, 1)
            """);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetSummary_ReturnsOnlyTestUserData()
    {
        var response = await _client!.GetAsync("/api/analytics/summary");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var summary = JsonSerializer.Deserialize<JsonElement>(json, JsonSerializerOptions.Web);

        // test-user has 2 events totalling 450 tokens; must not include other-user's 500
        summary.GetProperty("totalTokens").GetDouble().ShouldBe(450);
        summary.GetProperty("messageCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetDaily_ReturnsOnlyTestUserDailyRollups()
    {
        var response = await _client!.GetAsync("/api/analytics/daily");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var daily = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        daily.ShouldNotBeNull();
        daily.Length.ShouldBe(1);
        daily[0].GetProperty("tokens").GetDouble().ShouldBe(450);
    }

    [Fact]
    public async Task GetSessions_ReturnsOnlyTestUserSessions()
    {
        var response = await _client!.GetAsync("/api/analytics/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        sessions.Length.ShouldBe(1);
        sessions[0].GetProperty("sessionId").GetString().ShouldBe("tu-sess-1");
    }

    [Fact]
    public async Task GetModels_ReturnsOnlyTestUserModelData()
    {
        var response = await _client!.GetAsync("/api/analytics/models");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        models.ShouldNotBeNull();
        models.Length.ShouldBe(1);
        models[0].GetProperty("modelId").GetString().ShouldBe("claude-sonnet");
    }

    [Fact]
    public async Task Export_ReturnsOnlyTestUserEvents()
    {
        var response = await _client!.GetAsync("/api/analytics/export");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        rows.ShouldNotBeNull();
        rows.Length.ShouldBe(2);
        rows.ShouldAllBe(r => r.GetProperty("eventId").GetString()!.StartsWith("tu-"));
    }

    private static void ExecuteNonQuery(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A standalone <see cref="WebApplicationFactory{TEntryPoint}"/> that enables analytics
    /// and configures test authentication (sub=test-user) for all requests.
    /// </summary>
    private sealed class AnalyticsWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-analytics-isolation-{Guid.NewGuid():N}.db");
        private readonly string _analyticsDbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-analytics-isolation-analytics-{Guid.NewGuid():N}.db");
        private readonly string _natsDataDir =
            Path.Combine(Path.GetTempPath(), $"fleet-analytics-isolation-nats-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_natsDataDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Fleet:DatabasePath", _dbPath);
            builder.UseSetting("Fleet:AnalyticsDatabasePath", _analyticsDbPath);
            builder.UseSetting("Fleet:Nats:DataDirectory", _natsDataDir);
            builder.UseSetting("Fleet:Nats:StreamName", $"fleet-anly-{Guid.NewGuid().ToString("N")[..12]}");
            builder.UseSetting("Fleet:Nats:TenantPrefix", $"tenant.anly-{Guid.NewGuid().ToString("N")[..8]}");
            builder.UseSetting("Fleet:AnalyticsEnabled", "true");
            builder.UseSetting("Fleet:Port", "0");
            builder.UseSetting("Fleet:Auth:Enabled", "true");
            builder.UseSetting("Fleet:Auth:Authority", "https://example.test");
            builder.UseSetting("Fleet:Auth:ClientId", "test-client");
            builder.UseSetting("Fleet:Auth:ClientSecret", "test-secret");
            builder.UseSetting("Fleet:Auth:AllowedOrigins:0", "http://localhost:3001");

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });

            builder.UseSetting("Authentication:DefaultScheme", "Test");
            builder.UseSetting("Authentication:DefaultAuthenticateScheme", "Test");
            builder.UseSetting("Authentication:DefaultChallengeScheme", "Test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TryDelete(_dbPath);
            TryDelete(_analyticsDbPath);
            TryDelete($"{_dbPath}-wal");
            TryDelete($"{_dbPath}-shm");
            TryDeleteDirectory(_natsDataDir);
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

                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);

                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }
    }
}
