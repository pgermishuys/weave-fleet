using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// API-level tenant isolation tests for instance endpoints.
/// Seeds instances for two users directly into the DB, then verifies that
/// the authenticated test user (sub=test-user) cannot access other-user's instances.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class InstanceEndpointTenantIsolationTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private InstanceIsolationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new InstanceIsolationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Trigger app startup
        await _client.GetAsync("/api/sessions");

        // Seed instances for two users directly into the main DB
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = dbFactory.CreateConnection();

        ExecuteNonQuery(conn, """
            INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-test', 0, NULL, '/ws-test', '', 'running', '2026-03-01T00:00:00+00:00', 'test-user'),
                   ('inst-other', 0, NULL, '/ws-other', '', 'running', '2026-03-01T00:00:00+00:00', 'other-user')
            """);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetInstanceModels_ReturnsNotFound_ForOtherUsersInstance()
    {
        var response = await _client!.GetAsync("/api/instances/inst-other/models");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInstanceCommands_ReturnsNotFound_ForOtherUsersInstance()
    {
        var response = await _client!.GetAsync("/api/instances/inst-other/commands");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInstanceAgents_ReturnsNotFound_ForOtherUsersInstance()
    {
        var response = await _client!.GetAsync("/api/instances/inst-other/agents");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FindInstanceFiles_ReturnsNotFound_ForOtherUsersInstance()
    {
        var response = await _client!.GetAsync("/api/instances/inst-other/find/files?q=test");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInstanceModels_ReturnsNotFound_ForOwnInstanceNotInTracker()
    {
        // The instance is in the DB but not registered in the in-memory InstanceTracker,
        // so it should return NotFound (instance not running).
        var response = await _client!.GetAsync("/api/instances/inst-test/models");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInstanceModels_DoesNotDoublePrefixQualifiedOpenCodeProviders()
    {
        var tracker = _factory!.Services.GetRequiredService<InstanceTracker>();
        tracker.Register(
            "inst-test",
            new ProviderListingHarnessSession(
                "inst-test",
                "opencode",
                [
                    new ProviderInfo
                    {
                        Id = "anthropic",
                        Name = "Anthropic",
                        Models =
                        [
                            new ModelInfo { Id = "claude-opus-4.6", Name = "Claude Opus 4.6" }
                        ]
                    },
                    new ProviderInfo
                    {
                        Id = "openrouter",
                        Name = "OpenRouter",
                        Models =
                        [
                            new ModelInfo { Id = "anthropic/claude-opus-4.6", Name = "Claude Opus 4.6" }
                        ]
                    },
                    new ProviderInfo
                    {
                        Id = "openrouter/anthropic",
                        Name = "OpenRouter Anthropic",
                        Models =
                        [
                            new ModelInfo { Id = "claude-sonnet-4", Name = "Claude Sonnet 4" }
                        ]
                    }
                ]));

        var response = await _client!.GetAsync("/api/instances/inst-test/models");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var providers = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        providers.ShouldNotBeNull();
        providers.Length.ShouldBe(3);

        var modelIds = providers
            .SelectMany(static provider => provider.GetProperty("models").EnumerateArray())
            .Select(static model => model.GetProperty("id").GetString())
            .ToArray();

        modelIds.ShouldContain("claude-opus-4.6");
        modelIds.ShouldContain("anthropic/claude-opus-4.6");
        modelIds.ShouldContain("claude-sonnet-4");
        modelIds.ShouldNotContain("openrouter/anthropic/claude-opus-4.6");
        modelIds.ShouldNotContain("openrouter/claude-sonnet-4");
    }

    private static void ExecuteNonQuery(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class ProviderListingHarnessSession(
        string instanceId,
        string harnessType,
        IReadOnlyList<ProviderInfo> providers) : IHarnessSession
    {
        public string InstanceId { get; } = instanceId;
        public int? ProcessId => null;
        public string? ResumeToken => null;
        public string HarnessType { get; } = harnessType;
        public HarnessSessionStatus Status => HarnessSessionStatus.Running;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct) => Task.CompletedTask;

        public Task SendCommandAsync(CommandOptions options, CancellationToken ct) => Task.CompletedTask;

        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
            => Task.FromResult(new MessagePage([], false));

        public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield break;
        }

        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
            => Task.FromResult(new HealthCheckResult(true, null));

        public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

        public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
            => Task.FromResult(providers);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InstanceIsolationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-instance-isolation-{Guid.NewGuid():N}.db");
        private readonly string _analyticsDbPath =
            Path.Combine(Path.GetTempPath(), $"fleet-instance-isolation-analytics-{Guid.NewGuid():N}.db");

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
