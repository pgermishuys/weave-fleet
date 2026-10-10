using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Fleet's MCP server offers the OpenCode plugin's tools, as the plugin describes them, so the model sees the same tools on
/// every harness. The catalog (<c>fleet-tools.json</c>) is a copy; these fail when the plugin changes and the copy doesn't.
/// </summary>
public sealed partial class FleetToolCatalogPluginTests
{
    private static readonly string Plugin = Encoding.UTF8.GetString(OpenCodeFleetPlugin.ReadEmbedded());

    /// <summary>Every string literal in the plugin, in order, as the strings they are.</summary>
    private static readonly List<string> Literals = [.. StringLiteral().Matches(Plugin).Select(match => JsonSerializer.Deserialize<string>(match.Value)!)];

    [Fact]
    public void The_catalog_has_every_tool_the_plugin_has_and_no_other()
    {
        var plugin = ToolName().Matches(Plugin).Select(match => match.Groups[1].Value).ToList();

        FleetToolCatalog.All.Select(tool => tool.Name).ShouldBe(plugin);
    }

    [Fact]
    public void Each_tool_is_described_as_the_plugin_describes_it()
    {
        foreach (var tool in FleetToolCatalog.All)
            IsWritten(tool.Description).ShouldBeTrue($"{tool.Name}'s description isn't the plugin's.");
    }

    [Fact]
    public void Each_tools_input_is_the_plugins()
    {
        foreach (var tool in FleetToolCatalog.All)
        {
            tool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
            var properties = tool.InputSchema.GetProperty("properties").EnumerateObject().ToList();
            // OpenCode makes every argument of a plugin tool required.
            tool.InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ShouldBe(properties.Select(p => p.Name));

            foreach (var property in properties)
            {
                Plugin.ShouldContain($"{property.Name}", customMessage: $"{tool.Name}.{property.Name} isn't in the plugin.");
                Literals.ShouldContain(property.Value.GetProperty("type").GetString()!);
                IsWritten(property.Value.GetProperty("description").GetString()!).ShouldBeTrue($"{tool.Name}.{property.Name}'s description isn't the plugin's.");
                if (property.Value.TryGetProperty("enum", out var values))
                {
                    foreach (var value in values.EnumerateArray())
                        Literals.ShouldContain(value.GetString()!);
                }
            }
        }
    }

    [Fact]
    public void The_switchable_tools_are_the_ones_the_plugin_adds_only_with_their_switch()
    {
        FleetToolCatalog.All.Where(tool => tool.Requires is not null).Select(tool => (tool.Name, tool.Requires)).ShouldBe(
        [
            ("fleet_walkthrough_show", "walkthrough"),
            ("fleet_browser_read", "browser"),
            ("fleet_browser_act", "browser"),
            ("fleet_message", "sessionMessages"),
            ("fleet_machine_list", "agentHandoff"),
            ("fleet_session_start", "agentHandoff"),
            ("fleet_memory_save", "memory"),
            ("fleet_memory_forget", "memory"),
            ("fleet_step_done", "workflowStep"),
            ("fleet_mod_write", "mods"),
            ("fleet_mod_check", "mods"),
            ("fleet_mod_reload", "mods"),
            ("fleet_mod_test", "mods"),
            ("fleet_mod_keep", "mods"),
            ("fleet_mod_list", "mods"),
        ]);
    }

    [Fact]
    public void Each_input_a_switch_adds_is_the_plugins_too()
    {
        var switched = FleetToolCatalog.All
            .Where(tool => tool.SwitchedProperties.ValueKind == System.Text.Json.JsonValueKind.Object)
            .SelectMany(tool => tool.SwitchedProperties.EnumerateObject().SelectMany(group =>
                group.Value.EnumerateObject().Select(property => (Tool: tool.Name, Switch: group.Name, property.Name, property.Value))))
            .ToList();

        switched.Select(p => (p.Tool, p.Switch, p.Name)).ShouldBe(
        [
            ("fleet_session_read", "agentHandoff", "machine"),
            ("fleet_message", "agentHandoff", "machine"),
        ]);
        foreach (var property in switched)
        {
            Literals.ShouldContain(property.Value.GetProperty("type").GetString()!);
            IsWritten(property.Value.GetProperty("description").GetString()!).ShouldBeTrue($"{property.Tool}.{property.Name}'s description isn't the plugin's.");
        }
    }

    /// <summary>Whether <paramref name="text"/> is a literal in the plugin, or a run of them joined with spaces.</summary>
    private static bool IsWritten(string text)
    {
        for (var start = 0; start < Literals.Count; start++)
        {
            if (!text.StartsWith(Literals[start], StringComparison.Ordinal))
                continue;
            var joined = new StringBuilder(Literals[start]);
            for (var next = start + 1; joined.Length < text.Length && next < Literals.Count; next++)
                joined.Append(' ').Append(Literals[next]);
            if (joined.ToString() == text)
                return true;
        }

        return false;
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    /// <summary>A tool in the plugin: <c>fleet_canvas_open: {</c>.</summary>
    [GeneratedRegex(@"^\s+(fleet_[a-z_]+): \{", RegexOptions.Multiline)]
    private static partial Regex ToolName();
}
