using System.Text;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Terminals;
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
        File.Exists(Path.Combine(skills, "fleet", "SKILL.md")).ShouldBeTrue();
        File.WriteAllText(Path.Combine(skills, "fleet", "old.md"), "no longer shipped");

        OpenCode2FleetFiles.InstallSkills(_dataDirectory);

        File.Exists(Path.Combine(skills, "fleet", "old.md")).ShouldBeFalse();
    }

    [Fact]
    public void The_folder_of_the_skill_Fleet_renamed_is_removed()
    {
        var skills = OpenCode2FleetFiles.InstallSkills(_dataDirectory);
        Directory.CreateDirectory(Path.Combine(skills, "fleet-api"));
        File.WriteAllText(Path.Combine(skills, "fleet-api", "SKILL.md"), "from an older Fleet");

        OpenCode2FleetFiles.InstallSkills(_dataDirectory);

        Directory.Exists(Path.Combine(skills, "fleet-api")).ShouldBeFalse();
        File.Exists(Path.Combine(skills, "fleet", "SKILL.md")).ShouldBeTrue();
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
    public void An_owners_own_version_of_a_skill_takes_the_place_of_Fleets_and_Fleets_comes_back_without_it()
    {
        var names = OpenCode2FleetFiles.BuiltInSkillNames.Order(StringComparer.Ordinal).ToList();
        var (mine, fleets) = (names[0], names[1]);
        var version = $"---\nname: {mine}\ndescription: My version.\n---\n\nMy way.\n";

        var folder = OpenCode2FleetFiles.SyncBuiltInSkills(
            _dataDirectory, "local-user", [mine, fleets], new Dictionary<string, string> { [mine] = version });

        File.ReadAllText(Path.Combine(folder, mine, "SKILL.md")).ShouldBe(version);
        File.ReadAllText(Path.Combine(folder, fleets, "SKILL.md")).ShouldNotContain("My way.");

        OpenCode2FleetFiles.SyncBuiltInSkills(_dataDirectory, "local-user", [mine, fleets]);

        File.ReadAllText(Path.Combine(folder, mine, "SKILL.md")).ShouldNotContain("My way.");
    }

    [Fact]
    public void An_owners_own_version_of_fleet_plan_keeps_the_runtime_Fleet_ships_beside_it()
    {
        // The skill tells the agent to pack and lint with runtime/pack.mjs from its own folder.
        var version = "---\nname: fleet-plan\ndescription: My version.\n---\n\nMy way.\n";

        var folder = OpenCode2FleetFiles.SyncBuiltInSkills(
            _dataDirectory, "local-user", ["fleet-plan"], new Dictionary<string, string> { ["fleet-plan"] = version });

        File.ReadAllText(Path.Combine(folder, "fleet-plan", "SKILL.md")).ShouldBe(version);
        foreach (var file in new[] { "htmlplan.js", "htmlplan.css", "pack.mjs" })
            File.ReadAllText(Path.Combine(folder, "fleet-plan", "runtime", file))
                .ShouldBe(File.ReadAllText(RepoPath("opencode", "built-in-skills", "fleet-plan", "runtime", file)));
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
    public void The_plugin_imports_only_node_built_ins_and_every_tool_is_outside_code_mode()
    {
        var plugin = Plugin();

        ImportLine().Matches(plugin).Select(match => match.Groups[1].Value).ShouldAllBe(module => module.StartsWith("node:"));
        ImportLine().Count(plugin).ShouldBe(ImportStatement().Count(plugin), "every import names its module on one line");
        plugin.ShouldContain("options: { codemode: false, ...options }");
        plugin.ShouldContain("export default {\n  id: \"fleet\",");
    }

    [Fact]
    public void The_shell_environment_names_what_to_remove_as_null_and_what_to_put_back_as_its_value()
    {
        var changes = new Dictionary<string, string?>
        {
            ["OPENCODE_SERVER_PASSWORD"] = null,
            ["OPENCODE_DB"] = "/home/me/mine.db",
            ["FLEET_BRIDGE_TOKEN"] = null,
        };

        OpenCode2FleetFiles.BuildShellEnvironment(changes)
            .ShouldBe("""{"FLEET_BRIDGE_TOKEN":null,"OPENCODE_DB":"/home/me/mine.db","OPENCODE_SERVER_PASSWORD":null}""");
    }

    [Fact]
    public void The_plugin_applies_the_shell_environment_to_every_shell_V2_starts()
    {
        var plugin = Plugin();

        plugin.ShouldContain($"process.env.{OpenCode2FleetFiles.ShellEnvironmentVariable}");
        plugin.ShouldContain("ctx.shell.hook(\"create.before\"");
    }

    [Fact]
    public void The_plugin_tells_every_shell_and_every_session_its_own_server_pid()
    {
        var plugin = Plugin();

        plugin.ShouldContain("const serverPid = process.pid");
        plugin.ShouldContain("event.env.FLEET_HARNESS_SERVER_PID = String(serverPids[0])");
        plugin.ShouldContain("input.system.push({ type: \"text\", text: serverNotice })");
        plugin.ShouldContain("Never kill it; exclude it from any kill-by-name or kill-by-pattern cleanup.");
    }

    [Fact]
    public void The_plugin_names_only_its_own_pid_when_Fleet_started_it()
    {
        var (pids, notice, nodePid) = RunPluginUnder(hostPid: Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        pids.ShouldBe([nodePid]);
        notice.ShouldContain($"is pid {nodePid} (");
    }

    [Fact]
    public void The_plugin_names_the_launcher_too_when_something_between_Fleet_and_it_started_it()
    {
        // The test process stands in for the bun shim: it's the plugin's parent, and Fleet's pid is someone else's.
        var (pids, notice, nodePid) = RunPluginUnder(hostPid: "1234567");

        pids.ShouldBe([Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), nodePid]);
        notice.ShouldContain($"is pid {pids[0]} and {nodePid} (");
        notice.ShouldContain("Never kill either");
    }

    [Fact]
    public void The_plugin_names_only_itself_when_Fleet_didnt_say_its_pid()
        => RunPluginUnder(hostPid: null).Pids.Length.ShouldBe(1);

    [Fact]
    public void Fleet_hands_the_plugin_its_pid_and_takes_it_out_of_the_agents_shells()
    {
        TerminalEnvironment.IsFleetOwned(OpenCode2FleetFiles.HostPidVariable).ShouldBeTrue();
        File.ReadAllText(RepoPath("src", "WeaveFleet.Infrastructure", "Harnesses", "OpenCode2", "OpenCode2HarnessRuntime.cs"))
            .ShouldContain("environment[OpenCode2FleetFiles.HostPidVariable]");
    }

    /// <summary>Runs the plugin's setup under node, a child of this process, with a fake V2 context; returns what it told shells and sessions.</summary>
    private (string[] Pids, string Notice, string NodePid) RunPluginUnder(string? hostPid)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dataDirectory, $"plugin-run-{Guid.NewGuid():N}")).FullName;
        File.WriteAllText(Path.Combine(folder, "index.js"), Plugin());
        File.WriteAllText(Path.Combine(folder, "package.json"), "{\"type\":\"module\"}");
        File.WriteAllText(Path.Combine(folder, "run.mjs"), """
            import plugin from "./index.js"
            const hooks = {}
            const ctx = {
              location: { directory: process.cwd() },
              session: { hook: async (n, f) => { hooks["session." + n] = f } },
              shell: { hook: async (n, f) => { hooks["shell." + n] = f } },
              tool: { hook: async () => {}, transform: async () => {} },
            }
            await plugin.setup(ctx)
            const input = { sessionID: "s", system: [] }
            hooks["session.context"](input)
            const event = { env: {}, command: "x" }
            hooks["shell.create.before"](event)
            console.log(JSON.stringify({ system: input.system.map((p) => p.text), env: event.env, pid: process.pid }))
            """);

        var psi = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = folder };
        psi.ArgumentList.Add("run.mjs");
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("FLEET_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        if (hostPid is not null)
            psi.Environment[OpenCode2FleetFiles.HostPidVariable] = hostPid;
        using var process = System.Diagnostics.Process.Start(psi).ShouldNotBeNull();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, output);

        using var json = System.Text.Json.JsonDocument.Parse(output);
        var env = json.RootElement.GetProperty("env");
        var pids = env.GetProperty("FLEET_HARNESS_SERVER_PIDS").GetString()!.Split(' ');
        var notice = json.RootElement.GetProperty("system").EnumerateArray().Select(e => e.GetString()!).Single(t => t.Contains("Never kill "));
        env.GetProperty("FLEET_HARNESS_SERVER_PID").GetString().ShouldBe(pids[0]);
        return (pids, notice, json.RootElement.GetProperty("pid").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_plugin_has_the_same_tools_as_the_OpenCode_plugin()
    {
        var v1 = Encoding.UTF8.GetString(OpenCode2FleetFiles.Read("opencode/fleet-canvas.ts"));

        // OpenCode 2 drives the agent's browser through its own browser plugin (Fleet is its attachment), so only
        // OpenCode's plugin has Fleet's tools for it.
        string[] openCodeOnly = ["fleet_browser_act", "fleet_browser_read"];
        ToolNames(v1).ShouldBe([.. ToolNames(Plugin()).Concat(openCodeOnly).Order(StringComparer.Ordinal)]);
        ToolNames(Plugin()).ShouldContain("fleet_message");
        ToolNames(Plugin()).ShouldContain("fleet_memory_save");
    }

    private static string Plugin() => Encoding.UTF8.GetString(OpenCode2FleetFiles.Read(OpenCode2FleetFiles.PluginResource)).ReplaceLineEndings("\n");

    private static List<string> ToolNames(string source)
        => ToolName().Matches(source).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"""?(fleet_(?:canvas|app|browser|message|memory|machine|session)[a-z_]*)""?\s*[,:]")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"^\s*import\s.*\sfrom\s+""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    [GeneratedRegex(@"^\s*import\s", RegexOptions.Multiline)]
    private static partial Regex ImportStatement();

    private static string RepoPath(string first, params string[] rest)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "opencode2", "fleet")))
            directory = directory.Parent;

        return Path.Combine([directory!.FullName, first, .. rest]);
    }
}
