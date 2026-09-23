using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Tools;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Skills;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Skills and tools install globally (~/.config/opencode) or into a repository (&lt;repo&gt;/.opencode),
/// and removing them deletes what was installed. Everything runs against a temporary home folder.
/// </summary>
public sealed class InstallScopeEndpointTests : IAsyncDisposable
{
    private const int LocalSource = 2;

    private readonly string _home = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"fleet-install-scope-{Guid.NewGuid():N}")).FullName;

    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public InstallScopeEndpointTests()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services =>
        {
            var paths = new HarnessInstallPaths(_home);
            var skillStore = new JsonSkillManifestStore(_home);
            services.RemoveAll<HarnessInstallPaths>();
            services.RemoveAll<ISkillManifestStore>();
            services.RemoveAll<ISkillSyncEngine>();
            services.RemoveAll<IToolManifestStore>();
            services.RemoveAll<IToolInstaller>();
            services.AddSingleton(paths);
            services.AddSingleton<ISkillManifestStore>(skillStore);
            services.AddSingleton<ISkillSyncEngine>(new SkillSyncEngine(skillStore, paths, NullLogger<SkillSyncEngine>.Instance));
            services.AddSingleton<IToolManifestStore>(new JsonToolManifestStore(_home));
            services.AddSingleton<IToolInstaller>(new ToolInstaller(paths, NullLogger<ToolInstaller>.Instance));
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallTool_Global_LandsInOpenCodesGlobalToolsFolder()
    {
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(_home, ".config", "opencode", "tools", "sample-tool.ts");
        File.Exists(installed).ShouldBeTrue();

        var tool = (await GetJsonAsync("/api/tools")).GetProperty("tools").EnumerateArray().ShouldHaveSingleItem();
        tool.GetProperty("scope").GetString().ShouldBe("global");
        tool.GetProperty("installedPath").GetString().ShouldBe(installed);
    }

    [Fact]
    public async Task InstallTool_Project_LandsInTheRepository_AndRemoveDeletesIt()
    {
        var repo = await CreateRepositoryAsync("my-repo");
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        var install = await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source, "project", repo));

        install.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(repo, ".opencode", "tools", "sample-tool.ts");
        File.Exists(installed).ShouldBeTrue();
        File.Exists(Path.Combine(_home, ".config", "opencode", "tools", "sample-tool.ts")).ShouldBeFalse();

        var remove = await _client.DeleteAsync($"/api/tools/sample-tool?scope=project&projectPath={Uri.EscapeDataString(repo)}");

        remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(installed).ShouldBeFalse();
        (await GetJsonAsync("/api/tools")).GetProperty("tools").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task InstallTool_GlobalAndProject_AreSeparateInstalls()
    {
        var repo = await CreateRepositoryAsync("my-repo");
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source, "project", repo))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await _client.DeleteAsync("/api/tools/sample-tool")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        File.Exists(Path.Combine(_home, ".config", "opencode", "tools", "sample-tool.ts")).ShouldBeFalse();
        File.Exists(Path.Combine(repo, ".opencode", "tools", "sample-tool.ts")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallTool_WhenAFileFleetDidNotWriteIsThere_Returns409AndRecordsNothing()
    {
        WriteFile(Path.Combine(_home, ".config", "opencode", "tools", "sample-tool.ts"), "// mine");
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetJsonAsync("/api/tools")).GetProperty("tools").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task InstallTool_IntoARepositoryOutsideTheWorkspaceRoots_Returns400()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_home, "outside", "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, ".git"));
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source, "project", outside));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Directory.Exists(Path.Combine(outside, ".opencode")).ShouldBeFalse();
    }

    [Fact]
    public async Task InstallTool_WithAnUnknownScope_Returns400()
    {
        var source = WriteFile(Path.Combine(_home, "downloads", "sample-tool.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool("sample-tool", source, "everywhere"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Skills ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallSkill_WithNoHarnesses_InstallsForOpenCode()
    {
        var source = CreateSkillSource("my-skill");

        var response = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill("my-skill", source));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(_home, ".config", "opencode", "skills", "my-skill");
        File.Exists(Path.Combine(installed, "SKILL.md")).ShouldBeTrue();

        var skill = (await GetJsonAsync("/api/skills")).GetProperty("skills").EnumerateArray().ShouldHaveSingleItem();
        skill.GetProperty("scope").GetString().ShouldBe("global");
        skill.GetProperty("installedPaths").EnumerateArray().Select(p => p.GetString()).ShouldBe([installed]);
    }

    [Fact]
    public async Task InstallSkill_Project_LandsInTheRepository_AndRemoveDeletesIt()
    {
        var repo = await CreateRepositoryAsync("my-repo");
        var source = CreateSkillSource("my-skill");

        var install = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill("my-skill", source, "project", repo));

        install.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(repo, ".opencode", "skills", "my-skill");
        File.Exists(Path.Combine(installed, "SKILL.md")).ShouldBeTrue();

        var skill = (await GetJsonAsync("/api/skills")).GetProperty("skills").EnumerateArray().ShouldHaveSingleItem();
        skill.GetProperty("scope").GetString().ShouldBe("project");
        skill.GetProperty("projectPath").GetString().ShouldBe(repo);

        var remove = await _client.DeleteAsync($"/api/skills/my-skill?scope=project&projectPath={Uri.EscapeDataString(repo)}");

        remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(installed).ShouldBeFalse();
        Directory.Exists(source).ShouldBeTrue("the source folder stays");
    }

    [Fact]
    public async Task InstallSkill_WhenAFolderFleetDidNotWriteIsThere_Returns409AndRecordsNothing()
    {
        WriteFile(Path.Combine(_home, ".config", "opencode", "skills", "my-skill", "SKILL.md"), "# mine");
        var source = CreateSkillSource("my-skill");

        var response = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill("my-skill", source));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetJsonAsync("/api/skills")).GetProperty("skills").GetArrayLength().ShouldBe(0);
        File.ReadAllText(Path.Combine(_home, ".config", "opencode", "skills", "my-skill", "SKILL.md")).ShouldBe("# mine");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_home, "src")).FullName;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var workspaceRoots = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
            var added = await workspaceRoots.AddRootAsync(root);
            if (added.IsFailure && !added.Error.Code.Contains("Conflict", StringComparison.Ordinal))
                throw new InvalidOperationException(added.Error.Description);
        }

        var repo = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return WorkspaceRootService.CanonicalizePath(repo);
    }

    private string CreateSkillSource(string name)
    {
        var dir = Path.Combine(_home, "my-skills", name);
        WriteFile(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n");
        return dir;
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
    }

    private static object NativeTool(string name, string localPath, string? scope = null, string? projectPath = null) => new
    {
        name,
        toolType = "native",
        source = LocalSource,
        command = (string?)null,
        args = (string[]?)null,
        env = (Dictionary<string, string>?)null,
        repoUrl = (string?)null,
        @ref = (string?)null,
        subPath = (string?)null,
        localPath,
        scope,
        projectPath
    };

    private static object LocalSkill(string name, string localPath, string? scope = null, string? projectPath = null) => new
    {
        name,
        source = LocalSource,
        repoUrl = (string?)null,
        @ref = (string?)null,
        subPath = (string?)null,
        localPath,
        targetHarnesses = (string[]?)null,
        scope,
        projectPath
    };
}
