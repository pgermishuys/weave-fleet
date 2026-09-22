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
    }

    [Fact]
    public void An_owners_built_in_skills_folder_holds_exactly_the_skills_they_turned_on()
    {
        var names = OpenCode2FleetFiles.BuiltInSkillNames.Order(StringComparer.Ordinal).ToList();
        var (on, off) = (names[0], names[1]);

        var folder = OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "local-user", [on]);

        folder.ShouldBe(Path.Combine(_dataDirectory, "opencode2", "built-in-skills", OpenCode2FleetFiles.OwnerFolder("local-user")));
        Directory.GetDirectories(folder).Select(Path.GetFileName).ShouldBe([on]);
        File.Exists(Path.Combine(folder, on, "SKILL.md")).ShouldBeTrue();

        // Switched: the folder is the same, its contents follow.
        OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "local-user", [off]).ShouldBe(folder);
        Directory.GetDirectories(folder).Select(Path.GetFileName).ShouldBe([off]);

        // Nothing on: the folder stays, empty, so the servers that name it still watch it.
        OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "local-user", []).ShouldBe(folder);
        Directory.Exists(folder).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(folder).ShouldBeEmpty();
    }

    [Fact]
    public void Each_owner_has_a_built_in_skills_folder_of_their_own()
    {
        var name = OpenCode2FleetFiles.BuiltInSkillNames.Order(StringComparer.Ordinal).First();

        var mine = OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "owner-1", [name]);
        var theirs = OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "owner-2", []);

        mine.ShouldNotBe(theirs);
        File.Exists(Path.Combine(mine, name, "SKILL.md")).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(theirs).ShouldBeEmpty();
    }

    [Fact]
    public void Built_in_skills_left_from_before_owner_folders_are_removed()
    {
        var name = OpenCode2FleetFiles.BuiltInSkillNames.Order(StringComparer.Ordinal).First();
        var old = Path.Combine(_dataDirectory, "opencode2", "built-in-skills", name);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "SKILL.md"), "old");

        OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "local-user", [name]);

        Directory.Exists(old).ShouldBeFalse();
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
