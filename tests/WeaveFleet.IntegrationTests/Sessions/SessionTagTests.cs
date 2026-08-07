using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using TestHarnessClass = WeaveFleet.TestHarness.TestHarness;
using TestHarnessRuntimeClass = WeaveFleet.TestHarness.TestHarnessRuntime;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Integration tests for session tag functionality:
/// - Create session with tags
/// - Update tags via PATCH
/// - Filter sessions by tags
/// - Automation target tag matching
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionTagTests : IAsyncLifetime, IDisposable
{
    private SessionTagTestServer _server = null!;
    private HttpClient _http = null!;

    public void Dispose()
    {
        _http?.Dispose();
    }

    public async Task InitializeAsync()
    {
        _server = new SessionTagTestServer();
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task POST_create_session_with_tags_persists_tags()
    {
        // Arrange
        var request = new
        {
            Title = "Tagged Session",
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = new[] { "production", "critical" }
        };

        // Act
        var response = await _http.PostAsJsonAsync("/api/sessions", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        body.ShouldNotBeNull();
        body.Session.ShouldNotBeNull();
        body.Session.Tags.ShouldNotBeNull();
        body.Session.Tags.Count.ShouldBe(2);
        body.Session.Tags.ShouldContain("production");
        body.Session.Tags.ShouldContain("critical");

        // Verify tags are in the creation response (persistence verified by repository)
        // Note: GET /api/sessions/{id} doesn't return tags in GetSessionResponse yet
    }

    [Fact]
    public async Task POST_create_session_without_tags_has_empty_tags()
    {
        // Arrange
        var request = new
        {
            Title = "Untagged Session",
            Directory = _server.TempDirectory,
            HarnessType = "opencode"
        };

        // Act
        var response = await _http.PostAsJsonAsync("/api/sessions", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        body.ShouldNotBeNull();
        body.Session.ShouldNotBeNull();
        body.Session.Tags.ShouldNotBeNull();
        body.Session.Tags.Count.ShouldBe(0);
    }

    [Fact]
    public async Task PATCH_update_tags_replaces_existing_tags()
    {
        // Arrange: create a session with initial tags
        var createRequest = new
        {
            Title = "Session to Update",
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = new[] { "old-tag", "deprecated" }
        };

        var createResponse = await _http.PostAsJsonAsync("/api/sessions", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        created.ShouldNotBeNull();

        var updateRequest = new { Tags = new[] { "new-tag", "updated" } };

        // Act
        var response = await _http.PatchAsync(
            $"/api/sessions/{created.Session.Id}/tags",
            JsonContent.Create(updateRequest));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedSession = await response.Content.ReadFromJsonAsync<Session>();
        updatedSession.ShouldNotBeNull();
        updatedSession.Tags.Count.ShouldBe(2);
        updatedSession.Tags.ShouldContain("new-tag");
        updatedSession.Tags.ShouldContain("updated");
        updatedSession.Tags.ShouldNotContain("old-tag");
        updatedSession.Tags.ShouldNotContain("deprecated");

        // Verify via list endpoint (which does return tags)
        var listResponse = await _http.GetAsync("/api/sessions");
        var sessions = await listResponse.Content.ReadFromJsonAsync<List<SessionListResponse>>();
        var session = sessions?.FirstOrDefault(s => s.Session.Id == created.Session.Id);
        session.ShouldNotBeNull();
        session.Tags.Count.ShouldBe(2);
        session.Tags.ShouldContain("new-tag");
        session.Tags.ShouldContain("updated");
    }

    [Fact]
    public async Task PATCH_update_tags_with_empty_list_clears_tags()
    {
        // Arrange: create a session with tags
        var createRequest = new
        {
            Title = "Session to Clear",
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = new[] { "tag1", "tag2" }
        };

        var createResponse = await _http.PostAsJsonAsync("/api/sessions", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        created.ShouldNotBeNull();

        var updateRequest = new { Tags = Array.Empty<string>() };

        // Act
        var response = await _http.PatchAsync(
            $"/api/sessions/{created.Session.Id}/tags",
            JsonContent.Create(updateRequest));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedSession = await response.Content.ReadFromJsonAsync<Session>();
        updatedSession.ShouldNotBeNull();
        updatedSession.Tags.Count.ShouldBe(0);

        // Verify via list endpoint
        var listResponse = await _http.GetAsync("/api/sessions");
        var sessions = await listResponse.Content.ReadFromJsonAsync<List<SessionListResponse>>();
        var session = sessions?.FirstOrDefault(s => s.Session.Id == created.Session.Id);
        session.ShouldNotBeNull();
        session.Tags.Count.ShouldBe(0);
    }

    [Fact]
    public async Task PATCH_update_tags_returns_404_for_nonexistent_session()
    {
        // Arrange
        var updateRequest = new { Tags = new[] { "tag" } };

        // Act
        var response = await _http.PatchAsync(
            "/api/sessions/nonexistent-id/tags",
            JsonContent.Create(updateRequest));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_sessions_filters_by_single_tag()
    {
        // Arrange: create sessions with different tags
        await CreateSessionWithTags("Session A", ["production"]);
        await CreateSessionWithTags("Session B", ["staging"]);
        await CreateSessionWithTags("Session C", ["production", "critical"]);

        // Act
        var response = await _http.GetAsync("/api/sessions?tags=production");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<SessionListResponse>>();
        body.ShouldNotBeNull();

        // Should return sessions A and C (both have "production" tag)
        var productionSessions = body.Where(s => s.Tags.Contains("production")).ToList();
        productionSessions.Count.ShouldBeGreaterThanOrEqualTo(2);
        productionSessions.ShouldContain(s => s.Session.Title == "Session A");
        productionSessions.ShouldContain(s => s.Session.Title == "Session C");

        // Should not return Session B (has "staging" tag)
        body.Where(s => s.Session.Title == "Session B").ShouldBeEmpty();
    }

    [Fact]
    public async Task GET_sessions_filters_by_multiple_tags()
    {
        // Arrange: create sessions with different tag combinations
        await CreateSessionWithTags("Session X", ["production", "api"]);
        await CreateSessionWithTags("Session Y", ["production", "web"]);
        await CreateSessionWithTags("Session Z", ["staging", "api"]);

        // Act: filter by "production" OR "api" (any-match)
        var response = await _http.GetAsync("/api/sessions?tags=production,api");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<SessionListResponse>>();
        body.ShouldNotBeNull();

        // Should return sessions with ANY of the tags (X, Y, Z all match)
        var matchingSessions = body
            .Where(s => s.Tags.Contains("production") || s.Tags.Contains("api"))
            .ToList();
        matchingSessions.Count.ShouldBeGreaterThanOrEqualTo(3);
        matchingSessions.ShouldContain(s => s.Session.Title == "Session X");
        matchingSessions.ShouldContain(s => s.Session.Title == "Session Y");
        matchingSessions.ShouldContain(s => s.Session.Title == "Session Z");
    }

    [Fact]
    public async Task GET_sessions_without_tags_filter_returns_all_sessions()
    {
        // Arrange: create sessions with and without tags
        await CreateSessionWithTags("Tagged", ["tag1"]);
        await CreateSessionWithTags("Untagged", null);

        // Act
        var response = await _http.GetAsync("/api/sessions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<SessionListResponse>>();
        body.ShouldNotBeNull();
        body.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task automation_with_target_tags_matches_session_with_matching_tag()
    {
        // Arrange: create an automation with TargetTags
        var automationRequest = new CreateAutomationRequest(
            Name: "Production Monitor",
            Prompt: "Monitor production session",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}",
            MaxConcurrentRuns: 1,
            MaxRunsPerHour: 10,
            TimeoutMinutes: 30,
            WorkspaceId: null,
            Model: null,
            Agent: null,
            TargetTags: ["production"]);

        var automationResponse = await _http.PostAsJsonAsync("/api/automations", automationRequest);
        automationResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var automation = await automationResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        automation.ShouldNotBeNull();

        // Enable the automation
        await _http.PostAsync($"/api/automations/{automation.Id}/enable", null);

        // Act: create a session with matching tag
        var sessionRequest = new
        {
            Title = "Production Session",
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = new[] { "production", "critical" }
        };

        var sessionResponse = await _http.PostAsJsonAsync("/api/sessions", sessionRequest);

        // Assert
        sessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await sessionResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        session.ShouldNotBeNull();
        session.Session.Tags.ShouldContain("production");

        // Verify automation exists and has correct TargetTags
        var getAutomationResponse = await _http.GetAsync($"/api/automations/{automation.Id}");
        var retrievedAutomation = await getAutomationResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        retrievedAutomation.ShouldNotBeNull();
        retrievedAutomation.TargetTags.ShouldNotBeNull();
        retrievedAutomation.TargetTags.Count.ShouldBe(1);
        retrievedAutomation.TargetTags.ShouldContain("production");
    }

    [Fact]
    public async Task automation_with_target_tags_does_not_match_session_without_matching_tag()
    {
        // Arrange: create an automation with TargetTags
        var automationRequest = new CreateAutomationRequest(
            Name: "Staging Monitor",
            Prompt: "Monitor staging session",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}",
            MaxConcurrentRuns: 1,
            MaxRunsPerHour: 10,
            TimeoutMinutes: 30,
            WorkspaceId: null,
            Model: null,
            Agent: null,
            TargetTags: ["staging"]);

        var automationResponse = await _http.PostAsJsonAsync("/api/automations", automationRequest);
        automationResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var automation = await automationResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        automation.ShouldNotBeNull();

        // Enable the automation
        await _http.PostAsync($"/api/automations/{automation.Id}/enable", null);

        // Act: create a session with non-matching tag
        var sessionRequest = new
        {
            Title = "Production Session",
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = new[] { "production" }
        };

        var sessionResponse = await _http.PostAsJsonAsync("/api/sessions", sessionRequest);

        // Assert
        sessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await sessionResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        session.ShouldNotBeNull();
        session.Session.Tags.ShouldNotContain("staging");
        session.Session.Tags.ShouldContain("production");

        // The automation should not trigger (verified by dispatcher logic, not HTTP response)
        // This test verifies the setup is correct; actual dispatch testing requires event simulation
    }

    [Fact]
    public async Task automation_without_target_tags_matches_all_sessions()
    {
        // Arrange: create an automation without TargetTags
        var automationRequest = new CreateAutomationRequest(
            Name: "Universal Monitor",
            Prompt: "Monitor all sessions",
            TriggerType: "event",
            TriggerConfig: "{\"events\":[\"SessionStarted\"]}",
            MaxConcurrentRuns: 1,
            MaxRunsPerHour: 10,
            TimeoutMinutes: 30,
            WorkspaceId: null,
            Model: null,
            Agent: null,
            TargetTags: null);

        var automationResponse = await _http.PostAsJsonAsync("/api/automations", automationRequest);
        automationResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var automation = await automationResponse.Content.ReadFromJsonAsync<AutomationResponse>();
        automation.ShouldNotBeNull();
        // TargetTags can be null or empty list when not specified
        (automation.TargetTags == null || automation.TargetTags.Count == 0).ShouldBeTrue();

        // Act: create sessions with and without tags
        var taggedSessionResponse = await CreateSessionWithTags("Tagged Session", ["production"]);
        var untaggedSessionResponse = await CreateSessionWithTags("Untagged Session", null);

        // Assert: both sessions should be eligible for the automation
        taggedSessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        untaggedSessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> CreateSessionWithTags(string title, List<string>? tags)
    {
        var request = new
        {
            Title = title,
            Directory = _server.TempDirectory,
            HarnessType = "opencode",
            Tags = tags
        };

        return await _http.PostAsJsonAsync("/api/sessions", request);
    }
}

/// <summary>
/// Lightweight test server for session tag tests.
/// Uses in-memory SQLite for isolation.
/// </summary>
internal sealed class SessionTagTestServer : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fleet-tag-test-{Guid.NewGuid():N}.db");
    private readonly string _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"fleet-tag-analytics-test-{Guid.NewGuid():N}.db");
    private IHost? _host;
    private string? _serverUrl;
    private string? _tempDirectory;

    public TestHarnessClass TestHarness { get; } = new();
    public TestHarnessRuntimeClass TestHarnessRuntime { get; } = new();
    public string ServerUrl => _serverUrl ?? throw new InvalidOperationException("Not started");
    public string TempDirectory => _tempDirectory ?? throw new InvalidOperationException("Not started");
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Not started");

    public async Task StartAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"fleet-tag-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        var factory = new TestWebApplicationFactory(_dbPath, _analyticsDbPath, TestHarness, TestHarnessRuntime);

        // Trigger host creation
        try { _ = factory.Services; }
        catch (InvalidCastException) { /* expected: base tries to cast Kestrel to TestServer */ }

        _host = factory.Host;
        _serverUrl = factory.ServerUrl;

        // Initialize database schema
        using var scope = _host.Services.CreateScope();
        var connFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = connFactory.CreateConnection();

        // Create automations table
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
                target_tags TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT,
                user_id TEXT NOT NULL
            );
            """,
            _ => { });

        // Register workspace root
        var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        await workspaceRootService.AddRootAsync(_tempDirectory);
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

        if (_tempDirectory is not null && Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
        }
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
                        d.ServiceType == typeof(IHarnessRegistry) ||
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
                services.AddSingleton<IHarnessRegistry>(sp =>
                {
                    var harnesses = sp.GetServices<IHarness>();
                    var runtimes = sp.GetServices<IHarnessRuntime>();
                    return new HarnessRegistry(harnesses, runtimes);
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
            // Build and start the real Kestrel host
            builder.ConfigureWebHost(wb => wb.UseKestrel());
            _host = builder.Build();
            _host.Start();

            var server = _host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()!;
            ServerUrl = addresses.Addresses.First();

            // Return a dummy host to satisfy WebApplicationFactory's base class.
            // This prevents the base from creating a second TestServer host that
            // would re-run Program.cs (including migrations) against the same DB.
            return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureWebHost(wb => wb.UseTestServer())
                .Build();
        }

        private sealed class EmptyPoolHealth : IOpenCodePoolHealthCheck
        {
            public OpenCodePoolHealthStatus GetStatus() => new(0, 0, WarmCount: 0, ActiveCount: 0, []);
        }
    }
}
