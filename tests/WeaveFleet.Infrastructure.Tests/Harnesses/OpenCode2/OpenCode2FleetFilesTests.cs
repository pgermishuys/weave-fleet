using System.Text;
using System.Text.RegularExpressions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>Fleet's plugin and skills for OpenCode 2 servers, and the config that loads them.</summary>
public sealed partial class OpenCode2FleetFilesTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fleet-opencode2-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public void The_plugin_is_written_as_index_js_in_a_folder_of_its_own_because_V2_loads_folders()
    {
        var folder = OpenCode2FleetFiles.InstallPlugin(_dataDirectory);

        folder.ShouldBe(Path.Combine(_dataDirectory, "opencode2", "fleet"));
        Directory.GetFiles(folder).ShouldBe([Path.Combine(folder, "index.js")]);
        File.ReadAllText(Path.Combine(folder, "index.js")).ShouldBe(File.ReadAllText(RepoPath("opencode2", "fleet", "index.js")));
    }

    [Fact]
    public void An_identical_plugin_is_left_alone_and_a_different_one_replaced()
    {
        var index = Path.Combine(OpenCode2FleetFiles.InstallPlugin(_dataDirectory), "index.js");
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(index, written);

        OpenCode2FleetFiles.InstallPlugin(_dataDirectory);
        File.GetLastWriteTimeUtc(index).ShouldBe(written);

        File.WriteAllText(index, "// an older plugin");
        OpenCode2FleetFiles.InstallPlugin(_dataDirectory);
        File.ReadAllBytes(index).ShouldBe(OpenCode2FleetFiles.Read(OpenCode2FleetFiles.PluginResource));
    }

    [Fact]
    public void The_skills_are_written_with_their_folders_and_stale_files_removed()
    {
        var skills = OpenCode2FleetFiles.InstallSkills(_dataDirectory);
        File.Exists(Path.Combine(skills, "fleet-api", "SKILL.md")).ShouldBeTrue();
        File.WriteAllText(Path.Combine(skills, "fleet-api", "old.md"), "no longer shipped");

        OpenCode2FleetFiles.InstallSkills(_dataDirectory);

        File.Exists(Path.Combine(skills, "fleet-api", "old.md")).ShouldBeFalse();
        var builtIn = OpenCode2FleetFiles.InstallBuiltInSkills(_dataDirectory);
        foreach (var name in OpenCode2FleetFiles.BuiltInSkillNames)
            File.Exists(Path.Combine(builtIn, name, "SKILL.md")).ShouldBeTrue();
    }

    [Fact]
    public void The_built_in_skills_are_the_folders_Fleet_ships()
        => OpenCode2FleetFiles.BuiltInSkillNames.Order(StringComparer.Ordinal)
            .ShouldBe(Directory.GetDirectories(RepoPath("opencode", "built-in-skills")).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    [Fact]
    public void The_config_names_the_plugin_folder_and_the_skill_folders()
    {
        OpenCode2FleetFiles.BuildConfigContent("/data/opencode2/fleet", ["/data/opencode2/skills", "/data/opencode2/built-in-skills/fleet-run"])
            .ShouldBe("""{"plugins":["/data/opencode2/fleet"],"skills":["/data/opencode2/skills","/data/opencode2/built-in-skills/fleet-run"]}""");
        OpenCode2FleetFiles.BuildConfigContent(null, ["/s"]).ShouldBe("""{"skills":["/s"]}""");
        OpenCode2FleetFiles.BuildConfigContent(null, []).ShouldBeNull();
    }

    [Fact]
    public void The_plugin_has_no_imports_and_every_tool_is_outside_code_mode()
    {
        var plugin = Plugin();

        ImportLine().IsMatch(plugin).ShouldBeFalse();
        plugin.ShouldContain("options: { codemode: false, ...options }");
        plugin.ShouldContain("export default {\n  id: \"fleet\",");
    }

    [Fact]
    public void The_plugin_has_the_same_tools_as_the_OpenCode_plugin()
    {
        var v1 = Encoding.UTF8.GetString(OpenCode2FleetFiles.Read("opencode/fleet-canvas.ts"));

        ToolNames(Plugin()).ShouldBe(ToolNames(v1));
        ToolNames(Plugin()).ShouldContain("fleet_message");
    }

    private static string Plugin() => Encoding.UTF8.GetString(OpenCode2FleetFiles.Read(OpenCode2FleetFiles.PluginResource)).ReplaceLineEndings("\n");

    private static List<string> ToolNames(string source)
        => ToolName().Matches(source).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"""?(fleet_(?:canvas|app|browser|message)[a-z_]*)""?\s*[,:]")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"^\s*import\s", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    private static string RepoPath(string first, params string[] rest)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "opencode2", "fleet")))
            directory = directory.Parent;

        return Path.Combine([directory!.FullName, first, .. rest]);
    }
}
