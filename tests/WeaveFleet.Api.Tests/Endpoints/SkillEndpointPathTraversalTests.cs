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
    // ── POST /api/skills — path traversal rejection via name field ────────────

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

        var payload = System.Text.Json.JsonSerializer.Serialize(new { name, sourcePath = (string?)null });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/skills", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── GET /api/skills/{name} — encoded traversal via route parameter ────────

    [Theory]
    [InlineData("/api/skills/%2e%2e")]               // URL-encoded ".."
    [InlineData("/api/skills/foo%2fbar")]             // URL-encoded "foo/bar"
    [InlineData("/api/skills/foo%5cbar")]             // URL-encoded "foo\bar"
    public async Task GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal(string url)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        // Either 400 (our validation caught it) or 404 (framework resolved it away) — never 200
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    // ── Valid names still work ────────────────────────────────────────────────

    [Fact]
    public async Task GetSkill_Returns404_ForNonExistentValidName()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/skills/nonexistent-skill-xyz");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

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

        // Valid name should not be rejected as 400 — it gets Created or Conflict
        var payload = System.Text.Json.JsonSerializer.Serialize(new { name = "my-valid-test-skill", sourcePath = (string?)null });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/skills", content);

        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest);
    }
}
