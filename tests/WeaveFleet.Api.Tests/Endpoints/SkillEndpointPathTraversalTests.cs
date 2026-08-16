using System.Net;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Integration tests verifying that SkillEndpoints rejects path traversal attempts
/// and accepts valid skill names through the HTTP pipeline.
/// URL-level traversal (e.g. /api/skills/../..) is resolved by the framework's routing
/// before reaching the handler, so the primary attack vector is the POST body's Name field
/// and encoded route parameters that survive routing.
/// </summary>
public sealed class SkillEndpointPathTraversalTests
{
    // ── POST /api/skills/install — path traversal rejection via name field ────────────

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("name/with/slashes")]
    [InlineData("name\\with\\backslashes")]
    public async Task PostSkill_Returns400_ForTraversalName(string name)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            name,
            source = 1, // GitHub
            repoUrl = "https://github.com/example/test",
            @ref = (string?)null,
            subPath = (string?)null,
            localPath = (string?)null,
            targetHarnesses = (string[]?)null
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/skills/install", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── DELETE /api/skills/{name} — encoded traversal via route parameter ────────

    [Theory]
    [InlineData("/api/skills/foo%2fbar")]             // URL-encoded "foo/bar"
    [InlineData("/api/skills/foo%5cbar")]             // URL-encoded "foo\bar"
    public async Task DeleteSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal(string url)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        var response = await client.SendAsync(request);

        // Either 400 (our validation caught it) or 404 (framework resolved it away) — never 200
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    // ── Valid names still work ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSkill_Returns404_ForNonExistentValidName()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/skills/nonexistent-skill-xyz");
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostSkill_AcceptsValidNames()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        // Valid name should not be rejected as 400 — it may get 409 (Conflict) or other non-400 status
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            name = "my-valid-test-skill",
            source = 2, // Local
            repoUrl = (string?)null,
            @ref = (string?)null,
            subPath = (string?)null,
            localPath = "/tmp/nonexistent-skill-path",
            targetHarnesses = (string[]?)null
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/skills/install", content);

        // The name validation should pass (not 400 for invalid name).
        // It may return 400 for "Local path does not exist" which is a different validation,
        // so we just verify it's not a name-related 400 by checking the response body
        // Actually for a local source with non-existent path, it will be 400 "Local path does not exist"
        // which is acceptable — the name validation passed.
        // For simplicity, just verify it doesn't return the "Invalid skill name" error.
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("Invalid skill name");
    }
}
