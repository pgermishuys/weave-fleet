using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using static WeaveFleet.Infrastructure.Tests.Harnesses.AgentHandoffToolsTests;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// <see cref="AgentHandoffToolsTests"/> for OpenCode's own plugin (<c>fleet-canvas.ts</c>): agent hand-off and mods add
/// their tools and change nothing else, and <c>fleet_message</c> is there with a machine to hand work to.
/// </summary>
public sealed class OpenCodeAgentHandoffToolsTests
{
    private static string Plugin => Encoding.UTF8.GetString(OpenCodeFleetPlugin.ReadEmbedded());

    [Fact]
    public async Task Off_the_opencode_plugin_offers_what_it_did_before()
    {
        var plugin = Plugin;
        var off = await RunAsync(plugin, "fleet-canvas.ts", "opencode", handoff: false);
        var on = await RunAsync(plugin, "fleet-canvas.ts", "opencode", handoff: true);

        Names(on).ShouldBe([.. Names(off).Concat(AddedTools)], ignoreOrder: true);
        foreach (var name in ToolsWithMachine)
            Tool(on, name)["args"]!.AsObject().Remove("machine").ShouldBeTrue(name);
        JsonNode.DeepEquals(Without(on, AddedTools), off).ShouldBeTrue();
    }

    [Fact]
    public async Task With_a_machine_to_hand_work_to_fleet_message_is_there_even_with_messages_between_sessions_off()
    {
        Names(await RunAsync(Plugin, "fleet-canvas.ts", "opencode", handoff: false, messages: false)).ShouldNotContain("fleet_message");
        Names(await RunAsync(Plugin, "fleet-canvas.ts", "opencode", handoff: true, messages: false)).ShouldContain("fleet_message");
    }

    [Fact]
    public async Task The_mod_tools_are_in_the_plugin_only_with_FLEET_MODS_on()
    {
        var off = await RunAsync(Plugin, "fleet-canvas.ts", "opencode", handoff: false, mods: false);
        var on = await RunAsync(Plugin, "fleet-canvas.ts", "opencode", handoff: false, mods: true);

        Names(off).ShouldNotContain(name => ModTools.Contains(name));
        Names(on).Where(ModTools.Contains).ShouldBe(ModTools);
        // Added at the end, after the tools every process gets, and nothing else changes.
        Names(on).Take(Names(off).Count).ShouldBe(Names(off));
        JsonNode.DeepEquals(Without(on, ModTools), off).ShouldBeTrue();
    }
}
