using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using TestHarnessClass = WeaveFleet.TestHarness.TestHarness;
using TestHarnessRuntimeClass = WeaveFleet.TestHarness.TestHarnessRuntime;

namespace WeaveFleet.IntegrationTests.Automations;

/// <summary>
/// Integration tests for all 9 automation API endpoints.
/// Uses WebApplicationFactory to boot a real Kestrel server with in-memory SQLite.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AutomationEndpointTests : IAsyncLifetime, IDisposable
{
    private AutomationTestServer _server = null!;
    private HttpClient _http = null!;

    public void Dispose()
    {
        _http?.Dispose();
    }

    public async Task InitializeAsync()
    {
        _server = new AutomationTestServer();
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task POST_create_returns_201_with_response_body()
    {
        // Arrange
        var request = new CreateAutomationRequest(
            Name: "Test Automation",
            Prompt: "Analyze the session",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}",
            MaxConcurrentRuns: 2,
            MaxRunsPerHour: 5,
            TimeoutMinutes: 15,
            WorkspaceId: null,
            Model: "claude-3-5-sonnet-20241022",
            Agent: "loom");

        // Act
        var response = await _http.PostAsJsonAsync("/api/automations", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldStartWith("/api/automations/");

        var body = await response.Content.ReadFromJsonAsync<AutomationResponse>();
        body.ShouldNotBeNull();
        body.Id.ShouldNotBeNullOrEmpty();
        body.Name.ShouldBe("Test Automation");
        body.Prompt.ShouldBe("Analyze the session");
        body.TriggerType.ShouldBe("event");
        body.TriggerConfig.ShouldBe("{\"events\":[\"SessionStarted\"]}");
        body.MaxConcurrentRuns.ShouldBe(2);
        body.MaxRunsPerHour.ShouldBe(5);
        body.TimeoutMinutes.ShouldBe(15);
        body.IsEnabled.ShouldBeFalse(); // Automations are created disabled by default
        body.Model.ShouldBe("claude-3-5-sonnet-20241022");
        body.Agent.ShouldBe("loom");
        body.CreatedAt.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task PUT_update_returns_200_with_updated_body()
    {
        // Arrange: create an automation first
        var createRequest = new CreateAutomationRequest(
            Name: "Original Name",
            Prompt: "Original prompt",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        var updateRequest = new UpdateAutomationRequest(
            Name: "Updated Name",
            Prompt: "Updated prompt",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionIdled\"]}",
            MaxConcurrentRuns: 3,
            MaxRunsPerHour: 20,
            TimeoutMinutes: 45);

        // Act
        var response = await _http.PutAsJsonAsync($"/api/automations/{created.Id}", updateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<AutomationResponse>();
        body.ShouldNotBeNull();
        body.Id.ShouldBe(created.Id);
        body.Name.ShouldBe("Updated Name");
        body.Prompt.ShouldBe("Updated prompt");
        body.TriggerConfig.ShouldBe("{\"events\":[\"SessionIdled\"]}");
        body.MaxConcurrentRuns.ShouldBe(3);
        body.MaxRunsPerHour.ShouldBe(20);
        body.TimeoutMinutes.ShouldBe(45);
        body.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task GET_list_returns_200_with_array()
    {
        // Arrange: create two automations
        await _http.PostAsJsonAsync("/api/automations", new CreateAutomationRequest(
            Name: "Automation 1",
            Prompt: "Prompt 1",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}"));

        await _http.PostAsJsonAsync("/api/automations", new CreateAutomationRequest(
            Name: "Automation 2",
            Prompt: "Prompt 2",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionIdled\"]}"));

        // Act
        var response = await _http.GetAsync("/api/automations");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<AutomationListResponse>();
        body.ShouldNotBeNull();
        body.Automations.ShouldNotBeNull();
        body.Automations.Count.ShouldBeGreaterThanOrEqualTo(2);
        body.Automations.ShouldContain(a => a.Name == "Automation 1");
        body.Automations.ShouldContain(a => a.Name == "Automation 2");
    }

    [Fact]
    public async Task GET_by_id_returns_200_with_automation()
    {
        // Arrange: create an automation
        var createRequest = new CreateAutomationRequest(
            Name: "Get By ID Test",
            Prompt: "Test prompt",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        // Act
        var response = await _http.GetAsync($"/api/automations/{created.Id}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<AutomationResponse>();
        body.ShouldNotBeNull();
        body.Id.ShouldBe(created.Id);
        body.Name.ShouldBe("Get By ID Test");
        body.Prompt.ShouldBe("Test prompt");
    }

    [Fact]
    public async Task GET_by_id_returns_404_for_nonexistent_automation()
    {
        // Act
        var response = await _http.GetAsync("/api/automations/nonexistent-id");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DELETE_returns_204_and_marks_deleted()
    {
        // Arrange: create an automation
        var createRequest = new CreateAutomationRequest(
            Name: "To Delete",
            Prompt: "Will be deleted",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        // Act
        var deleteResponse = await _http.DeleteAsync($"/api/automations/{created.Id}");

        // Assert
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it's gone (soft-deleted)
        var getResponse = await _http.GetAsync($"/api/automations/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DELETE_returns_404_for_already_deleted_automation()
    {
        // Arrange: create and delete an automation
        var createRequest = new CreateAutomationRequest(
            Name: "Already Deleted",
            Prompt: "Already deleted",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        await _http.DeleteAsync($"/api/automations/{created.Id}");

        // Act: try to delete again
        var response = await _http.DeleteAsync($"/api/automations/{created.Id}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_enable_returns_204()
    {
        // Arrange: create and disable an automation
        var createRequest = new CreateAutomationRequest(
            Name: "To Enable",
            Prompt: "Will be enabled",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        await _http.PostAsync($"/api/automations/{created.Id}/disable", null);

        // Act
        var response = await _http.PostAsync($"/api/automations/{created.Id}/enable", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it's enabled
        var getResponse = await _http.GetAsync($"/api/automations/{created.Id}");
        var body = await getResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        body.ShouldNotBeNull();
        body.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task POST_disable_returns_204()
    {
        // Arrange: create an automation (enabled by default)
        var createRequest = new CreateAutomationRequest(
            Name: "To Disable",
            Prompt: "Will be disabled",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        // Act
        var response = await _http.PostAsync($"/api/automations/{created.Id}/disable", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it's disabled
        var getResponse = await _http.GetAsync($"/api/automations/{created.Id}");
        var body = await getResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        body.ShouldNotBeNull();
        body.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task POST_run_returns_202_accepted()
    {
        // Arrange: create an automation
        var createRequest = new CreateAutomationRequest(
            Name: "Manual Run Test",
            Prompt: "Manual trigger",
            TriggerType: "manual",
            TriggerConfig: "{}");

        var createResponse = await _http.PostAsJsonAsync("/api/automations", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        created.ShouldNotBeNull();

        // Act
        var response = await _http.PostAsync($"/api/automations/{created.Id}/run", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_run_returns_404_for_nonexistent_automation()
    {
        // Act
        var response = await _http.PostAsync("/api/automations/nonexistent-id/run", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_event_catalog_returns_array_of_strings()
    {
        // Act
        var response = await _http.GetAsync("/api/automations/event-catalog");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<string[]>();
        body.ShouldNotBeNull();
        body.Length.ShouldBeGreaterThan(0);
        body.ShouldContain("session.created");
        body.ShouldContain("session.idle");
        body.ShouldContain("message.created");
    }
}

/// <summary>
/// Lightweight test server that boots the real API with Kestrel.
/// Uses in-memory SQLite for isolation.
/// </summary>
internal sealed class AutomationTestServer : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fleet-automation-test-{Guid.NewGuid():N}.db");
    private readonly string _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"fleet-automation-analytics-test-{Guid.NewGuid():N}.db");
    private IHost? _host;
    private string? _serverUrl;

    public TestHarnessClass TestHarness { get; } = new();
    public TestHarnessRuntimeClass TestHarnessRuntime { get; } = new();
    public string ServerUrl => _serverUrl ?? throw new InvalidOperationException("Not started");
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Not started");

    public async Task StartAsync()
    {
        var factory = new TestWebApplicationFactory(_dbPath, _analyticsDbPath, TestHarness, TestHarnessRuntime);

        // Trigger host creation
        try { _ = factory.Services; }
        catch (InvalidCastException) { /* expected: base tries to cast Kestrel to TestServer */ }

        _host = factory.Host;
        _serverUrl = factory.ServerUrl;

        // Initialize database schema for automations table (missing migration)
        using var scope = _host.Services.CreateScope();
        var connFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            CREATE TABLE IF NOT EXISTS automations (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                prompt TEXT NOT NULL,
                trigger_type TEXT NOT NULL,
                trigger_config TEXT NOT NULL,
                max_concurrent_runs INTEGER NOT NULL DEFAULT 1,
                max_runs_per_hour INTEGER NOT NULL DEFAULT 10,
                timeout_minutes INTEGER NOT NULL DEFAULT 30,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                workspace_id TEXT,
                model TEXT,
                agent TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT,
                user_id TEXT NOT NULL
            );
            """,
            _ => { });

        // Register workspace root
        var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        await workspaceRootService.AddRootAsync(tempRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        TryDelete(_dbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
        TryDelete(_analyticsDbPath);
        TryDelete($"{_analyticsDbPath}-wal");
        TryDelete($"{_analyticsDbPath}-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        private readonly string _analyticsDbPath;
        private readonly TestHarnessClass _testHarness;
        private readonly TestHarnessRuntimeClass _testHarnessRuntime;
        private IHost? _host;

        public TestWebApplicationFactory(
            string dbPath, string analyticsDbPath,
            TestHarnessClass testHarness, TestHarnessRuntimeClass testHarnessRuntime)
        {
            _dbPath = dbPath;
            _analyticsDbPath = analyticsDbPath;
            _testHarness = testHarness;
            _testHarnessRuntime = testHarnessRuntime;
        }

        public IHost Host => _host ?? throw new InvalidOperationException("Not started");
        public string ServerUrl { get; private set; } = "";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove production harness registrations
                var toRemove = services
                    .Where(d =>
                        d.ServiceType == typeof(IHarness) ||
                        d.ServiceType == typeof(IHarnessRuntime) ||
                        d.ServiceType == typeof(OpenCodeHarness) ||
                        d.ServiceType == typeof(OpenCodeHarnessRuntime) ||
                        d.ServiceType == typeof(ClaudeCodeHarness) ||
                        d.ServiceType == typeof(ClaudeCodeHarnessRuntime))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);

                services.AddSingleton<IHarness>(_testHarness);
                services.AddSingleton<IHarnessRuntime>(sp =>
                {
                    _testHarnessRuntime.SetScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
                    return _testHarnessRuntime;
                });

                // Remove pool health check
                var poolHealth = services.Where(d => d.ServiceType == typeof(IOpenCodePoolHealthCheck)).ToList();
                foreach (var d in poolHealth) services.Remove(d);
                services.AddSingleton<IOpenCodePoolHealthCheck, EmptyPoolHealth>();

                // Replace FleetOptions and DB
                var existingOptions = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(FleetOptions) && d.Lifetime == ServiceLifetime.Singleton);
                if (existingOptions is not null) services.Remove(existingOptions);

                var connFactory = services.Where(d => d.ServiceType == typeof(IDbConnectionFactory)).ToList();
                foreach (var d in connFactory) services.Remove(d);

                var portAlloc = services.Where(d => d.ServiceType.Name == "PortAllocator").ToList();
                foreach (var d in portAlloc) services.Remove(d);

                var testOptions = new FleetOptions
                {
                    DatabasePath = _dbPath,
                    AnalyticsDatabasePath = _analyticsDbPath,
                    AnalyticsEnabled = false,
                    Port = 0,
                    Host = "127.0.0.1",
                    Auth = new AuthOptions { Enabled = false, TokenAuthEnabled = false },
                };

                services.AddSingleton(testOptions);
                services.AddSingleton(new PortAllocator(
                    testOptions.HarnessPortRangeStart, testOptions.HarnessPortRangeEnd));
                services.AddSingleton<IDbConnectionFactory>(
                    _ => new WeaveFleet.Infrastructure.Data.SqliteConnectionFactory(testOptions));
            });

            builder.UseUrls("http://127.0.0.1:0");
            builder.UseSetting("Urls", "http://127.0.0.1:0");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "false");
            builder.ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Urls"] = "http://127.0.0.1:0"
                });
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureWebHost(wb => wb.UseKestrel());
            _host = builder.Build();
            _host.Start();

            var server = _host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()!;
            ServerUrl = addresses.Addresses.First();

            return _host;
        }

        private sealed class EmptyPoolHealth : IOpenCodePoolHealthCheck
        {
            public OpenCodePoolHealthStatus GetStatus() => new(0, 0, WarmCount: 0, ActiveCount: 0, []);
        }
    }
}
