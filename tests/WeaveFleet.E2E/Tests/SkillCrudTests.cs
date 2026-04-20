using System.Net;
using System.Net.Http.Json;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Exercises the currently-supported skill lifecycle against the real backend.
/// The UI renders a skills section, but its contracts do not currently match the API,
/// so this test validates the backend flow directly for a reliable happy-path check.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class SkillCrudTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SkillCrudTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task SkillCrud_HappyPath_CompletesViaApiLifecycleAndFileEdit()
    {
        await WithFailureCapture(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N");
            var skillName = $"e2e-skill-{suffix}";
            var sourceDirectory = Path.Combine(Path.GetTempPath(), $"{skillName}-source");
            var installedDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".weave",
                "skills",
                skillName);
            var originalPrompt = $"# {skillName}\n\nOriginal instructions {suffix}";
            var updatedPrompt = $"# {skillName}\n\nEdited instructions {suffix}";

            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "prompt.md"), originalPrompt);

            using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };

            try
            {
                var createResponse = await http.PostAsJsonAsync("/api/skills", new
                {
                    name = skillName,
                    sourcePath = sourceDirectory
                });

                createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

                var createdSkill = await ReadJsonAsync<CreateSkillResponse>(createResponse.Content);
                createdSkill.Name.ShouldBe(skillName);
                createdSkill.Path.ShouldBe(installedDirectory);
                Directory.Exists(createdSkill.Path).ShouldBeTrue();

                var listResponse = await http.GetAsync("/api/skills");
                listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

                var listedSkills = await ReadJsonAsync<SkillListEntry[]>(listResponse.Content);
                var listedSkill = listedSkills.SingleOrDefault(skill => skill.Name == skillName);
                listedSkill.ShouldNotBeNull();
                listedSkill!.Path.ShouldBe(installedDirectory);
                listedSkill.HasPrompt.ShouldBeTrue();

                await File.WriteAllTextAsync(Path.Combine(installedDirectory, "prompt.md"), updatedPrompt);

                var detailResponse = await http.GetAsync($"/api/skills/{Uri.EscapeDataString(skillName)}");
                detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

                var skillDetail = await ReadJsonAsync<SkillDetailResponse>(detailResponse.Content);
                skillDetail.Name.ShouldBe(skillName);
                skillDetail.Path.ShouldBe(installedDirectory);
                skillDetail.Prompt.ShouldBe(updatedPrompt);

                var deleteResponse = await http.DeleteAsync($"/api/skills/{Uri.EscapeDataString(skillName)}");
                deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

                var listAfterDeleteResponse = await http.GetAsync("/api/skills");
                listAfterDeleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

                var skillsAfterDelete = await ReadJsonAsync<SkillListEntry[]>(listAfterDeleteResponse.Content);
                skillsAfterDelete.Any(skill => skill.Name == skillName).ShouldBeFalse();

                var detailAfterDeleteResponse = await http.GetAsync($"/api/skills/{Uri.EscapeDataString(skillName)}");
                detailAfterDeleteResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
            finally
            {
                TryDeleteDirectory(sourceDirectory);
                TryDeleteDirectory(installedDirectory);
            }
        });
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContent content)
    {
        var result = await content.ReadFromJsonAsync<T>();
        return result ?? throw new InvalidOperationException($"Expected JSON body for {typeof(T).Name}.");
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
            // Best-effort cleanup only.
        }
    }

    private sealed record CreateSkillResponse(string Name, string Path);

    private sealed record SkillListEntry(string Name, string Path, bool HasPrompt);

    private sealed record SkillDetailResponse(string Name, string Path, string? Prompt);
}
