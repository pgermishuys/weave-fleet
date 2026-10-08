using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>
/// Agent hand-off adds two tools and a <c>machine</c> on <c>fleet_message</c> and <c>fleet_session_read</c>, in all three
/// places a model gets Fleet's tools: both OpenCode plugins and the catalog Claude Code reads over MCP. With it off, each
/// offers exactly what it did before: the tools with it on, less those additions, and nothing else changed.
/// The plugins are run in Node as OpenCode loads them (the .NET test job has Node 22).
/// </summary>
public sealed class AgentHandoffToolsTests
{
    private static readonly string[] AddedTools = ["fleet_machine_list", "fleet_session_start"];
    private static readonly string[] ToolsWithMachine = ["fleet_message", "fleet_session_read"];

    [Fact]
    public void Off_the_mcp_catalog_offers_each_tool_exactly_as_the_catalog_has_it()
    {
        var off = FleetToolCatalog.For(Switches(handoff: false));
        var on = FleetToolCatalog.For(Switches(handoff: true));

        off.Select(tool => tool.Name).ShouldBe(on.Select(tool => tool.Name).Except(AddedTools));
        foreach (var tool in off)
            tool.InputSchema.GetRawText().ShouldBe(FleetToolCatalog.Find(tool.Name)!.InputSchema.GetRawText(), tool.Name);

        foreach (var name in ToolsWithMachine)
        {
            var schema = on.Single(tool => tool.Name == name).InputSchema;
            schema.GetProperty("properties").EnumerateObject().Last().Name.ShouldBe("machine");
            schema.GetProperty("required").EnumerateArray().Last().GetString().ShouldBe("machine");
            var without = JsonNode.Parse(schema.GetRawText())!.AsObject();
            without["properties"]!.AsObject().Remove("machine");
            without["required"]!.AsArray().RemoveAt(without["required"]!.AsArray().Count - 1);
            JsonNode.DeepEquals(without, JsonNode.Parse(FleetToolCatalog.Find(name)!.InputSchema.GetRawText())).ShouldBeTrue(name);
        }
    }

    [Fact]
    public async Task Off_the_opencode_plugin_offers_what_it_did_before()
    {
        var plugin = Encoding.UTF8.GetString(OpenCodeFleetPlugin.ReadEmbedded());
        var off = await RunAsync(plugin, "fleet-canvas.ts", "opencode", handoff: false);
        var on = await RunAsync(plugin, "fleet-canvas.ts", "opencode", handoff: true);

        Names(on).ShouldBe([.. Names(off).Concat(AddedTools)], ignoreOrder: true);
        foreach (var name in ToolsWithMachine)
            Tool(on, name)["args"]!.AsObject().Remove("machine").ShouldBeTrue(name);
        JsonNode.DeepEquals(Without(on, AddedTools), off).ShouldBeTrue();
    }

    [Fact]
    public async Task Off_the_opencode2_plugin_offers_what_it_did_before()
    {
        var plugin = Encoding.UTF8.GetString(OpenCode2FleetFiles.Read(OpenCode2FleetFiles.PluginResource));
        var off = await RunAsync(plugin, "index.mjs", "opencode2", handoff: false);
        var on = await RunAsync(plugin, "index.mjs", "opencode2", handoff: true);

        Names(on).ShouldBe([.. Names(off).Concat(AddedTools)], ignoreOrder: true);
        foreach (var name in ToolsWithMachine)
        {
            // Optional there: a session on this machine is the one without it.
            var input = Tool(on, name)["input"]!.AsObject();
            input["properties"]!.AsObject().Remove("machine").ShouldBeTrue(name);
            input["required"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldNotContain("machine");
        }

        JsonNode.DeepEquals(Without(on, AddedTools), off).ShouldBeTrue();
    }

    private static FleetToolSwitches Switches(bool handoff)
        => new(SessionMessages: true, Memory: true, WorkflowStep: true, Browser: true, Walkthrough: true, AgentHandoff: handoff);

    private static List<string> Names(JsonArray tools) => [.. tools.Select(tool => tool!["name"]!.GetValue<string>())];

    private static JsonObject Tool(JsonArray tools, string name) => tools.Single(tool => tool!["name"]!.GetValue<string>() == name)!.AsObject();

    private static JsonArray Without(JsonArray tools, string[] names)
        => [.. tools.Where(tool => !names.Contains(tool!["name"]!.GetValue<string>())).Select(tool => tool!.DeepClone())];

    /// <summary>
    /// Loads the plugin in Node, with messages between sessions on and hand-off on or off, and returns its tools as OpenCode
    /// would see them: <c>{name, description, args}</c> for OpenCode, <c>{name, description, input}</c> for OpenCode 2.
    /// </summary>
    private static async Task<JsonArray> RunAsync(string source, string fileName, string kind, bool handoff)
    {
        var folder = Directory.CreateTempSubdirectory("fleet-plugin-tools-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, fileName), source);
            await File.WriteAllTextAsync(Path.Combine(folder, "tools.mjs"), Driver);

            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in (string[])["--experimental-strip-types", "--no-warnings", "tools.mjs", fileName, kind])
                start.ArgumentList.Add(argument);
            start.Environment[SessionMessages.EnvironmentVariable] = "1";
            if (handoff)
                start.Environment[AgentHandoff.EnvironmentVariable] = "1";
            else
                start.Environment.Remove(AgentHandoff.EnvironmentVariable);

            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("These tests run Fleet's OpenCode plugins in Node; put node (22 or later) on PATH.", ex);
            }

            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                process.ExitCode.ShouldBe(0, await errors);
                return JsonNode.Parse(await output)!.AsArray();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private const string Driver = """
        const [file, kind] = process.argv.slice(2)
        const plugin = await import("./" + file)
        let tools
        if (kind === "opencode") {
          const hooks = await plugin.FleetCanvasPlugin({})
          tools = Object.entries(hooks.tool).map(([name, tool]) => ({ name, description: tool.description, args: tool.args }))
        } else {
          const added = []
          await plugin.default.setup({ tool: { transform: async (edit) => edit({ add: (tool) => added.push(tool) }) } })
          tools = added.map((tool) => ({ name: tool.name, description: tool.description, input: tool.input }))
        }
        process.stdout.write(JSON.stringify(tools))
        """;
}
