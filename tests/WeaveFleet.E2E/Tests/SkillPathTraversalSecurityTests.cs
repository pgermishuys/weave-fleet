using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests verifying that skill endpoints reject path traversal attempts.
/// These run with auth disabled to isolate the path validation behavior
/// from authentication concerns.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SkillPathTraversalSecurityTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SkillPathTraversalSecurityTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// GET /api/skills/{name} with a path traversal payload returns 400.
    /// </summary>
    [Fact]
    public async Task GetSkill_PathTraversal_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            // URL-encode "../../../etc/passwd" — the framework will decode it before routing
            var response = await Page.APIRequest.GetAsync(
                $"{ServerUrl}/api/skills/..%2F..%2F..%2Fetc%2Fpasswd");

            response.Status.ShouldBe(400);
        });
    }

    /// <summary>
    /// GET /api/skills/{name} with a backslash traversal payload returns 400.
    /// </summary>
    [Fact]
    public async Task GetSkill_BackslashTraversal_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.GetAsync(
                $"{ServerUrl}/api/skills/..%5C..%5C..%5Cwindows%5Csystem32");

            response.Status.ShouldBe(400);
        });
    }

    /// <summary>
    /// DELETE /api/skills/{name} with a path traversal payload returns 400.
    /// This is the most dangerous vector — recursive directory deletion.
    /// </summary>
    [Fact]
    public async Task DeleteSkill_PathTraversal_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.DeleteAsync(
                $"{ServerUrl}/api/skills/..%2F..%2Fimportant-dir");

            response.Status.ShouldBe(400);
        });
    }

    /// <summary>
    /// POST /api/skills with a traversal name in the JSON body returns 400.
    /// </summary>
    [Fact]
    public async Task CreateSkill_TraversalNameInBody_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.PostAsync(
                $"{ServerUrl}/api/skills",
                new Microsoft.Playwright.APIRequestContextOptions
                {
                    DataObject = new
                    {
                        name = "../escape",
                        instructions = "malicious payload"
                    }
                });

            response.Status.ShouldBe(400);
        });
    }

    /// <summary>
    /// GET /api/skills/{name} with a valid skill name returns 404 (not 400),
    /// confirming that legitimate names pass validation.
    /// </summary>
    [Fact]
    public async Task GetSkill_ValidName_DoesNotReturn400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.GetAsync(
                $"{ServerUrl}/api/skills/my-valid-skill");

            // Should be 404 (skill doesn't exist) — but NOT 400 (validation failure)
            response.Status.ShouldBe(404);
        });
    }
}
