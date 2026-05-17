using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// API-level tenant isolation tests for session endpoints.
/// Seeds sessions for two users directly into the DB, then verifies that
/// the authenticated test user (sub=test-user) cannot access other-user's sessions.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionEndpointTenantIsolationTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private SessionIsolationFactory? _factory;
    private HttpClient? _client;
    private string? _csrfToken;

    public async Task InitializeAsync()
    {
        _factory = new SessionIsolationFactory();
        // HandleCookies=true so the antiforgery state cookie (.WeaveFleet.Antiforgery) is
        // stored by the client and sent on subsequent requests — required for CSRF validation.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Trigger app startup and capture the CSRF token from the Set-Cookie header.
        // The antiforgery middleware emits .WeaveFleet.CSRF (readable, HttpOnly=false) on GET requests.
        var startupResponse = await _client.GetAsync("/api/sessions");
        _csrfToken = ExtractCookieValue(startupResponse, ".WeaveFleet.CSRF");

        // Seed sessions for two users directly into the main DB
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = dbFactory.CreateConnection();

        // Insert workspaces (required FK for sessions; use display_name not name)
        ExecuteNonQuery(conn, """
            INSERT OR IGNORE INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-test', '/ws-test', 'Test Workspace', '2026-03-01T00:00:00+00:00', 'test-user'),
                   ('ws-other', '/ws-other', 'Other Workspace', '2026-03-01T00:00:00+00:00', 'other-user')
            """);

        // Insert instances (required FK for sessions)
        ExecuteNonQuery(conn, """
            INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-test', 0, NULL, '/ws-test', '', 'stopped', '2026-03-01T00:00:00+00:00', 'test-user'),
                   ('inst-other', 0, NULL, '/ws-other', '', 'stopped', '2026-03-01T00:00:00+00:00', 'other-user')
            """);

        // Insert sessions for both users (opencode_session_id is required NOT NULL)
        ExecuteNonQuery(conn, """
            INSERT OR IGNORE INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES
              ('sess-test-1', 'ws-test', 'inst-test', 'oc-test-1', 'Test Session', 'stopped', '/ws-test',
               'stopped', 'active', '2026-03-01T10:00:00+00:00', 'test-user'),
              ('sess-other-1', 'ws-other', 'inst-other', 'oc-other-1', 'Other Session', 'stopped', '/ws-other',
               'stopped', 'active', '2026-03-01T12:00:00+00:00', 'other-user')
            """);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ListSessions_DoesNotReturnOtherUsersSessions()
    {
        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        // The response shape is SessionListResponse: session id is nested under session.id
        sessions.ShouldAllBe(s => s.GetProperty("session").GetProperty("id").GetString() == "sess-test-1");
        sessions.ShouldNotContain(s => s.GetProperty("session").GetProperty("id").GetString() == "sess-other-1");
    }

    [Fact]
    public async Task GetSession_ReturnsNotFoundForOtherUsersSession()
    {
        var response = await _client!.GetAsync("/api/sessions/sess-other-1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteSession_ReturnsNotFoundForOtherUsersSession()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/sessions/sess-other-1");
        AddCsrfHeader(request);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StopSession_ReturnsNotFoundForOtherUsersSession()
    {
        // POST /api/sessions/{id}/stop
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/sess-other-1/stop");
        AddCsrfHeader(request);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendCommand_WithLegacyUnqualifiedModel_ResolvesUniqueProviderFromCatalog()
    {
        var tracker = _factory!.Services.GetRequiredService<InstanceTracker>();
        var harness = new FakeHarnessSession("inst-test");
        tracker.Register("inst-test", harness);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/sess-test-1/command")
        {
            Content = JsonContent.Create(new
            {
                command = "start-work",
                model = "claude-sonnet-4"
            })
        };
        AddCsrfHeader(request);

        var instance = tracker.Get("inst-test");
        instance.ShouldNotBeNull();

        var providerSession = new ProviderListingHarnessSession(
            [
                new ProviderInfo
                {
                    Id = "openrouter",
                    Name = "OpenRouter",
                    Models = [new ModelInfo { Id = "claude-sonnet-4", Name = "Claude Sonnet 4" }]
                }
            ],
            harness);

        tracker.Register("inst-test", providerSession);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        harness.SendCommandCalls.Count.ShouldBe(1);
        harness.SendCommandCalls[0].ProviderId.ShouldBe("openrouter");
        harness.SendCommandCalls[0].ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public async Task SendCommand_WithAmbiguousLegacyModel_ReturnsBadRequest()
    {
        var tracker = _factory!.Services.GetRequiredService<InstanceTracker>();
        var harness = new FakeHarnessSession("inst-test");
        tracker.Register(
            "inst-test",
            new ProviderListingHarnessSession(
                [
                    new ProviderInfo
                    {
                        Id = "openrouter",
                        Name = "OpenRouter",
                        Models = [new ModelInfo { Id = "claude-sonnet-4", Name = "Claude Sonnet 4" }]
                    },
                    new ProviderInfo
                    {
                        Id = "anthropic",
                        Name = "Anthropic",
                        Models = [new ModelInfo { Id = "claude-sonnet-4", Name = "Claude Sonnet 4" }]
                    }
                ],
                harness));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/sess-test-1/command")
        {
            Content = JsonContent.Create(new
            {
                command = "start-work",
                model = "claude-sonnet-4"
            })
        };
        AddCsrfHeader(request);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        harness.SendCommandCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendCommand_WithUnknownStructuredModel_ReturnsBadRequest()
    {
        var tracker = _factory!.Services.GetRequiredService<InstanceTracker>();
        var harness = new FakeHarnessSession("inst-test");
        tracker.Register(
            "inst-test",
            new ProviderListingHarnessSession(
                [
                    new ProviderInfo
                    {
                        Id = "openrouter",
                        Name = "OpenRouter",
                        Models = [new ModelInfo { Id = "claude-sonnet-4", Name = "Claude Sonnet 4" }]
                    }
                ],
                harness));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/sess-test-1/command")
        {
            Content = JsonContent.Create(new
            {
                command = "start-work",
                model = new
                {
                    providerID = "openrouter",
                    modelID = "does-not-exist"
                }
            })
        };
        AddCsrfHeader(request);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        harness.SendCommandCalls.ShouldBeEmpty();
    }

    /// <summary>
    /// Adds the CSRF token header to a mutating request.
    /// The client stores the antiforgery state cookie (HandleCookies=true), so only
    /// the X-CSRF-Token header needs to be added manually.
    /// </summary>
    private void AddCsrfHeader(HttpRequestMessage request)
    {
        if (_csrfToken is not null)
            request.Headers.Add("X-CSRF-Token", _csrfToken);
    }

    /// <summary>
    /// Extracts the named cookie value from a response's Set-Cookie header.
    /// </summary>
    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return null;

        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';', StringComparison.Ordinal);
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header[(cookieName.Length + 1)..];
        }

        return null;
    }

    private static void ExecuteNonQuery(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class ProviderListingHarnessSession(
        IReadOnlyList<ProviderInfo> providers,
        FakeHarnessSession inner) : IHarnessSession
    {
        public string InstanceId => inner.InstanceId;
        public int? ProcessId => inner.ProcessId;
        public string? ResumeToken => inner.ResumeToken;
        public string HarnessType => inner.HarnessType;
        public HarnessSessionStatus Status => inner.Status;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);
        public Task DeleteAsync(CancellationToken ct) => inner.DeleteAsync(ct);
        public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct) => inner.SendPromptAsync(text, options, ct);
        public Task SendCommandAsync(CommandOptions options, CancellationToken ct) => inner.SendCommandAsync(options, ct);
        public Task AbortAsync(CancellationToken ct) => inner.AbortAsync(ct);
        public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct) => inner.AnswerQuestionAsync(requestId, answers, ct);
        public Task RejectQuestionAsync(string requestId, CancellationToken ct) => inner.RejectQuestionAsync(requestId, ct);
        public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct) => inner.GetMessagesAsync(query, ct);
        public IAsyncEnumerable<HarnessEvent> SubscribeAsync(CancellationToken ct) => inner.SubscribeAsync(ct);
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct) => inner.CheckHealthAsync(ct);
        public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) => inner.GetAgentsAsync(ct);
        public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct) => inner.GetCommandsAsync(ct);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct) => Task.FromResult(providers);
    }

    private sealed class SessionIsolationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-session-isolation-{Guid.NewGuid():N}.db");
        private readonly string _analyticsDbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-session-isolation-analytics-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Fleet:DatabasePath", _dbPath);
            builder.UseSetting("Fleet:AnalyticsDatabasePath", _analyticsDbPath);
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
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
