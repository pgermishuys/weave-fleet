using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.DTOs;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SmartLinkEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private static async Task<string> SeedSessionAsync(ApiWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = dbFactory.CreateConnection();

        conn.Execute("""
            INSERT OR IGNORE INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-sl-test', '/tmp', 'SL Test', '2026-01-01T00:00:00Z', 'local-user')
            """);

        conn.Execute("""
            INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-sl-test', 0, NULL, '/tmp', '', 'stopped', '2026-01-01T00:00:00Z', 'local-user')
            """);

        var sessionId = Guid.NewGuid().ToString();
        conn.Execute("""
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES
              (@Id, 'ws-sl-test', 'inst-sl-test', @OcId, 'SmartLink Test', 'stopped', '/tmp',
               'stopped', 'active', '2026-01-01T00:00:00Z', 'local-user')
            """,
            new { Id = sessionId, OcId = Guid.NewGuid().ToString() });

        return sessionId;
    }

    [Fact]
    public async Task SmartLinks_FullLifecycle_UpsertListAndDismiss()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);

        // Initially empty
        var listResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var links = await listResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(0);

        // Upsert a link
        var upsertResponse = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/smart-links",
            new UpsertSmartLinkRequest(
                "https://github.com/owner/repo/pull/1",
                "github",
                "pr",
                "owner/repo#1",
                "Fix bug",
                "open",
                "Open",
                null,
                false));
        upsertResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await upsertResponse.Content.ReadFromJsonAsync<SmartLinkDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.Title.ShouldBe("Fix bug");
        created.Status.ShouldBe("open");

        // List shows the link
        listResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links");
        links = await listResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(1);
        links[0].Url.ShouldBe("https://github.com/owner/repo/pull/1");

        // Upsert again with updated status (idempotent)
        var updateResponse = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/smart-links",
            new UpsertSmartLinkRequest(
                "https://github.com/owner/repo/pull/1",
                "github",
                "pr",
                "owner/repo#1",
                "Fix bug",
                "merged",
                "Merged",
                null,
                true));
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<SmartLinkDto>(JsonOptions);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("merged");
        updated.IsTerminal.ShouldBeTrue();

        // Dismiss the link
        var dismissResponse = await client.PatchAsync(
            $"/api/sessions/{sessionId}/smart-links/{created.Id}/dismiss",
            null);
        dismissResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // List no longer shows dismissed link
        listResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links");
        links = await listResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(0);

        // All endpoint shows dismissed link
        var allResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links/all");
        var allLinks = await allResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions);
        allLinks.ShouldNotBeNull();
        allLinks.Length.ShouldBe(1);
        allLinks[0].IsDismissed.ShouldBeTrue();
    }

    [Fact]
    public async Task SmartLinks_BulkUpsert_AddsMultipleLinks()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);

        var requests = new[]
        {
            new UpsertSmartLinkRequest("https://github.com/owner/repo/pull/1", "github", "pr", "owner/repo#1", "PR 1", "open", "Open", null, false),
            new UpsertSmartLinkRequest("https://github.com/owner/repo/issues/2", "github", "issue", "owner/repo#2", "Issue 2", "open", "Open", null, false),
        };

        var bulkResponse = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/smart-links/bulk", requests);
        bulkResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links");
        var links = await listResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(2);
    }

    [Fact]
    public async Task SmartLinks_UpsertForUnknownSession_ReturnsNotFound()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/nonexistent-session/smart-links",
            new UpsertSmartLinkRequest("https://github.com/owner/repo/pull/1", "github", "pr", "owner/repo#1", "PR", "open", "Open", null, false));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
