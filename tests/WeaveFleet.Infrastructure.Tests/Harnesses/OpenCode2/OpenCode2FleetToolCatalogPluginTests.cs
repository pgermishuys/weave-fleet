using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// The OpenCode 2 plugin offers the tools of Fleet's MCP server, described the same way, so the model sees the same
/// tools on every harness (the OpenCode 1 plugin's twin is <c>FleetToolCatalogPluginTests</c>). The catalog
/// (<c>fleet-tools.json</c>) is a copy; these fail when the plugin changes and the copy doesn't.
/// OpenCode 2 differs from the catalog in a few places that predate this test (it has its own browser, and words some
/// tools for itself); those are listed below, so any new difference fails.
/// </summary>
public sealed partial class OpenCode2FleetToolCatalogPluginTests
{
    private static readonly string Plugin = Encoding.UTF8.GetString(OpenCode2FleetFiles.Read(OpenCode2FleetFiles.PluginResource));

    /// <summary>Every string literal in the plugin, in order, as the strings they are.</summary>
    private static readonly List<string> Literals = [.. StringLiteral().Matches(Plugin).Select(match => JsonSerializer.Deserialize<string>(match.Value)!)];

    /// <summary>Tools in the catalog that OpenCode 2 doesn't have: V2 has its own browser (the browser plugin).</summary>
    private static readonly string[] NotInThePlugin = ["fleet_browser_read", "fleet_browser_act"];

    /// <summary>Tools the plugin describes in its own words.</summary>
    private static readonly string[] OwnDescription = ["fleet_app_start", "fleet_browser_screenshot"];

    /// <summary>Arguments the plugin describes in its own words, as <c>tool.argument</c>.</summary>
    private static readonly string[] OwnArgumentDescription =
    [
        "fleet_session_read.before", "fleet_session_read.limit", "fleet_session_read.machine", "fleet_message.machine",
        "fleet_session_start.branch", "fleet_session_start.harness", "fleet_memory_save.replaces",
    ];

    [Fact]
    public void The_catalog_has_every_tool_the_plugin_has_and_no_other()
    {
        var plugin = ToolName().Matches(Plugin).Select(match => match.Groups[1].Value).ToList();

        // The plugin adds some tools after others, by switch, so only the set is the same.
        FleetToolCatalog.All.Select(tool => tool.Name).Except(NotInThePlugin).ShouldBe(plugin, ignoreOrder: true);
    }

    [Fact]
    public void The_mod_tools_come_last_in_the_plugin()
    {
        var plugin = ToolName().Matches(Plugin).Select(match => match.Groups[1].Value).ToList();

        plugin.TakeLast(6).ShouldBe(FleetToolCatalog.All.Where(tool => tool.Requires == FleetToolSwitches.ModsSwitch).Select(tool => tool.Name));
    }

    [Fact]
    public void Each_tool_is_described_as_the_plugin_describes_it()
    {
        foreach (var tool in FleetToolCatalog.All.Where(tool => !NotInThePlugin.Contains(tool.Name) && !OwnDescription.Contains(tool.Name)))
            IsWritten(tool.Description).ShouldBeTrue($"{tool.Name}'s description isn't the plugin's.");
    }

    [Fact]
    public void Each_tools_input_is_the_plugins()
    {
        foreach (var tool in FleetToolCatalog.All.Where(tool => !NotInThePlugin.Contains(tool.Name)))
        {
            tool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
            foreach (var property in tool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Plugin.ShouldContain($"{property.Name}", customMessage: $"{tool.Name}.{property.Name} isn't in the plugin.");
                // OpenCode 2 has whole numbers as "integer".
                Literals.ShouldContain(property.Value.GetProperty("type").GetString() is "number" ? "integer" : property.Value.GetProperty("type").GetString()!);
                if (!OwnArgumentDescription.Contains($"{tool.Name}.{property.Name}"))
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
    public void Each_input_a_switch_adds_is_the_plugins_too()
    {
        var switched = FleetToolCatalog.All
            .Where(tool => tool.SwitchedProperties.ValueKind == JsonValueKind.Object)
            .SelectMany(tool => tool.SwitchedProperties.EnumerateObject().SelectMany(group =>
                group.Value.EnumerateObject().Select(property => (Tool: tool.Name, property.Name, property.Value))))
            .ToList();

        foreach (var property in switched)
        {
            Literals.ShouldContain(property.Value.GetProperty("type").GetString()!);
            if (!OwnArgumentDescription.Contains($"{property.Tool}.{property.Name}"))
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

    /// <summary>A tool in the plugin: <c>fleetTool(</c> and, on the next line, <c>"fleet_canvas_open",</c>.</summary>
    [GeneratedRegex(@"fleetTool\(\s*""(fleet_[a-z_]+)""")]
    private static partial Regex ToolName();
}
