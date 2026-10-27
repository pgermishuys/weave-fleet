using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Tools;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Skills;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Skills and tools install globally (~/.config/opencode) or into a repository (&lt;repo&gt;/.opencode),
/// and removing them deletes what was installed. Everything runs against a temporary home folder.
/// </summary>
public sealed class InstallScopeEndpointTests : IClassFixture<InstallScopeEndpointTests.Server>, IDisposable
{
    private const int LocalSource = 2;

    // One server and home folder for the class; each test installs under names of its own.
    private readonly string _home;
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private readonly string _tool;
    private readonly string _skill;
    private readonly string _repoName;

    public InstallScopeEndpointTests(Server server)
    {
        _home = server.Home;
        _factory = server.Factory;
        _client = _factory.CreateClient();
        (_tool, _skill, _repoName) = ($"sample-tool-{_id}", $"my-skill-{_id}", $"my-repo-{_id}");
    }

    public void Dispose() => _client.Dispose();

    public sealed class Server : IDisposable
    {
        public string Home { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"fleet-install-scope-{Guid.NewGuid():N}")).FullName;

        public ApiWebApplicationFactory Factory { get; }

        public Server()
        {
            Factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services =>
            {
                var paths = new HarnessInstallPaths(Home);
                var skillStore = new JsonSkillManifestStore(Home);
                services.RemoveAll<HarnessInstallPaths>();
                services.RemoveAll<ISkillManifestStore>();
                services.RemoveAll<ISkillSyncEngine>();
                services.RemoveAll<IToolManifestStore>();
                services.RemoveAll<IToolInstaller>();
                services.AddSingleton(paths);
                services.AddSingleton<ISkillManifestStore>(skillStore);
                services.AddSingleton<ISkillSyncEngine>(new SkillSyncEngine(skillStore, paths, NullLogger<SkillSyncEngine>.Instance));
                services.AddSingleton<IToolManifestStore>(new JsonToolManifestStore(Home));
                services.AddSingleton<IToolInstaller>(new ToolInstaller(paths, NullLogger<ToolInstaller>.Instance));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            try { Directory.Delete(Home, recursive: true); }
            catch (IOException) { }
        }
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallTool_Global_LandsInOpenCodesGlobalToolsFolder()
    {
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(_home, ".config", "opencode", "tools", $"{_tool}.ts");
        File.Exists(installed).ShouldBeTrue();

        var tool = (await OursAsync("/api/tools", "tools", _tool)).ShouldHaveSingleItem();
        tool.GetProperty("scope").GetString().ShouldBe("global");
        tool.GetProperty("installedPath").GetString().ShouldBe(installed);
    }

    [Fact]
    public async Task InstallTool_Project_LandsInTheRepository_AndRemoveDeletesIt()
    {
        var repo = await CreateRepositoryAsync(_repoName);
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        var install = await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source, "project", repo));

        install.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(repo, ".opencode", "tools", $"{_tool}.ts");
        File.Exists(installed).ShouldBeTrue();
        File.Exists(Path.Combine(_home, ".config", "opencode", "tools", $"{_tool}.ts")).ShouldBeFalse();

        var remove = await _client.DeleteAsync($"/api/tools/{_tool}?scope=project&projectPath={Uri.EscapeDataString(repo)}");

        remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(installed).ShouldBeFalse();
        (await OursAsync("/api/tools", "tools", _tool)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InstallTool_GlobalAndProject_AreSeparateInstalls()
    {
        var repo = await CreateRepositoryAsync(_repoName);
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source, "project", repo))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await _client.DeleteAsync($"/api/tools/{_tool}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        File.Exists(Path.Combine(_home, ".config", "opencode", "tools", $"{_tool}.ts")).ShouldBeFalse();
        File.Exists(Path.Combine(repo, ".opencode", "tools", $"{_tool}.ts")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallTool_WhenAFileFleetDidNotWriteIsThere_Returns409AndRecordsNothing()
    {
        WriteFile(Path.Combine(_home, ".config", "opencode", "tools", $"{_tool}.ts"), "// mine");
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OursAsync("/api/tools", "tools", _tool)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InstallTool_IntoARepositoryOutsideTheWorkspaceRoots_Returns400()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_home, "outside", "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, ".git"));
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source, "project", outside));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Directory.Exists(Path.Combine(outside, ".opencode")).ShouldBeFalse();
    }

    [Fact]
    public async Task InstallTool_WithAnUnknownScope_Returns400()
    {
        var source = WriteFile(Path.Combine(_home, "downloads", $"{_tool}.ts"), "export default {}");

        var response = await _client.PostAsJsonAsync("/api/tools/install", NativeTool(_tool, source, "everywhere"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Skills ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallSkill_WithNoHarnesses_InstallsForOpenCode()
    {
        var source = CreateSkillSource(_skill);

        var response = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill(_skill, source));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(_home, ".config", "opencode", "skills", _skill);
        File.Exists(Path.Combine(installed, "SKILL.md")).ShouldBeTrue();

        var skill = (await OursAsync("/api/skills", "skills", _skill)).ShouldHaveSingleItem();
        skill.GetProperty("scope").GetString().ShouldBe("global");
        skill.GetProperty("installedPaths").EnumerateArray().Select(p => p.GetString()).ShouldBe([installed]);
    }

    [Fact]
    public async Task InstallSkill_Project_LandsInTheRepository_AndRemoveDeletesIt()
    {
        var repo = await CreateRepositoryAsync(_repoName);
        var source = CreateSkillSource(_skill);

        var install = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill(_skill, source, "project", repo));

        install.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installed = Path.Combine(repo, ".opencode", "skills", _skill);
        File.Exists(Path.Combine(installed, "SKILL.md")).ShouldBeTrue();

        var skill = (await OursAsync("/api/skills", "skills", _skill)).ShouldHaveSingleItem();
        skill.GetProperty("scope").GetString().ShouldBe("project");
        skill.GetProperty("projectPath").GetString().ShouldBe(repo);

        var remove = await _client.DeleteAsync($"/api/skills/{_skill}?scope=project&projectPath={Uri.EscapeDataString(repo)}");

        remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(installed).ShouldBeFalse();
        Directory.Exists(source).ShouldBeTrue("the source folder stays");
    }

    [Fact]
    public async Task InstallSkill_WhenAFolderFleetDidNotWriteIsThere_Returns409AndRecordsNothing()
    {
        WriteFile(Path.Combine(_home, ".config", "opencode", "skills", _skill, "SKILL.md"), "# mine");
        var source = CreateSkillSource(_skill);

        var response = await _client.PostAsJsonAsync("/api/skills/install", LocalSkill(_skill, source));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OursAsync("/api/skills", "skills", _skill)).ShouldBeEmpty();
        File.ReadAllText(Path.Combine(_home, ".config", "opencode", "skills", _skill, "SKILL.md")).ShouldBe("# mine");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_home, "src")).FullName;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var workspaceRoots = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
            var added = await workspaceRoots.AddRootAsync(root);
            if (added.IsFailure && !added.Error.Code.Contains("Conflict", StringComparison.Ordinal)
                && !added.Error.Description.Contains("already registered", StringComparison.Ordinal))
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

    /// <summary>The listed entries with this test's name; the other tests' installs share the manifest.</summary>
    private async Task<List<JsonElement>> OursAsync(string url, string property, string name)
        => (await GetJsonAsync(url)).GetProperty(property).EnumerateArray().Where(e => e.GetProperty("name").GetString() == name).ToList();

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
