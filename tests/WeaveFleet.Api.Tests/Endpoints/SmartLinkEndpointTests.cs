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

    private static string SeedMentionedLink(ApiWebApplicationFactory factory, string sessionId)
    {
        using var scope = factory.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var id = Guid.NewGuid().ToString();
        conn.Execute("""
            INSERT INTO smart_links (id, session_id, url, provider_id, resource_type, resource_id, title, user_id, relationship)
            VALUES (@Id, @SessionId, 'https://github.com/owner/repo/issues/7', 'github', 'issue', 'owner/repo#7', 'owner/repo #7', 'local-user', 'mentioned')
            """,
            new { Id = id, SessionId = sessionId });
        return id;
    }

    [Fact]
    public async Task SmartLinks_AddListDismissAndRestore()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);

        var listResponse = await client.GetAsync($"/api/sessions/{sessionId}/smart-links");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await listResponse.Content.ReadFromJsonAsync<SmartLinkDto[]>(JsonOptions)).ShouldBeEmpty();

        // Attach a pull request; a trailing path and fragment are normalised away.
        var addResponse = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/smart-links",
            new AddSmartLinkRequest("https://github.com/owner/repo/pull/1/files#diff-1"));
        addResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await addResponse.Content.ReadFromJsonAsync<SmartLinkDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.Url.ShouldBe("https://github.com/owner/repo/pull/1");
        created.ResourceType.ShouldBe("pull_request");
        created.ResourceId.ShouldBe("owner/repo#1");
        created.Relationship.ShouldBe("pinned");

        // Adding the same number again doesn't create a second link.
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/smart-links", new AddSmartLinkRequest("https://github.com/owner/repo/pull/1"));
        var links = await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links", JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(1);

        var dismissResponse = await client.PatchAsync($"/api/sessions/{sessionId}/smart-links/{created.Id}/dismiss", null);
        dismissResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links", JsonOptions)).ShouldBeEmpty();

        var allLinks = await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links/all", JsonOptions);
        allLinks.ShouldNotBeNull();
        allLinks.Length.ShouldBe(1);
        allLinks[0].IsDismissed.ShouldBeTrue();

        // Attaching a dismissed link again shows it again.
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/smart-links", new AddSmartLinkRequest("https://github.com/owner/repo/pull/1"));
        links = await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links", JsonOptions);
        links.ShouldNotBeNull();
        links.Length.ShouldBe(1);
    }

    [Fact]
    public async Task SmartLinks_AddRejectsNonGitHubUrls()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/smart-links",
            new AddSmartLinkRequest("https://example.com/owner/repo/pull/1"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SmartLinks_PinAndUnpinMovesBetweenMentionedAndPinned()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);
        var linkId = SeedMentionedLink(factory, sessionId);

        (await client.PatchAsync($"/api/sessions/{sessionId}/smart-links/{linkId}/pin", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var links = await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links", JsonOptions);
        links.ShouldNotBeNull();
        links.Single().Relationship.ShouldBe("pinned");

        (await client.PatchAsync($"/api/sessions/{sessionId}/smart-links/{linkId}/unpin", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        links = await client.GetFromJsonAsync<SmartLinkDto[]>($"/api/sessions/{sessionId}/smart-links", JsonOptions);
        links.ShouldNotBeNull();
        links.Single().Relationship.ShouldBe("mentioned");

        (await client.PatchAsync($"/api/sessions/{sessionId}/smart-links/no-such-link/pin", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SmartLinks_Refresh_AcceptsForOwnSessionOnly()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = await SeedSessionAsync(factory);

        (await client.PostAsync($"/api/sessions/{sessionId}/smart-links/refresh", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsync("/api/sessions/nonexistent-session/smart-links/refresh", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SmartLinks_AddForUnknownSession_ReturnsNotFound()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/nonexistent-session/smart-links",
            new AddSmartLinkRequest("https://github.com/owner/repo/pull/1"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
