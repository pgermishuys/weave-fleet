using System.Collections;
using Shouldly;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Application.Tests.Terminals;

public sealed class TerminalEnvironmentTests
{
    [Theory]
    [InlineData("ELECTRON_RUN_AS_NODE")]
    [InlineData("ELECTRON_NO_ATTACH_CONSOLE")]
    [InlineData("Fleet__Desktop__Enabled")]
    public void Variables_from_the_desktop_app_never_reach_a_shell(string name)
    {
        var env = TerminalEnvironment.Build(new Hashtable { [name] = "1", ["HOME"] = "/home/me" }, windows: false);

        env.ShouldNotContainKey(name);
        env["HOME"].ShouldBe("/home/me");
    }

    [Fact]
    public void Electron_variables_are_removed_from_an_agent_s_environment()
    {
        var env = new Dictionary<string, string?> { ["ELECTRON_RUN_AS_NODE"] = "1", ["PATH"] = "/usr/bin" };

        TerminalEnvironment.RemoveFleetOwned(env);

        env.Keys.ShouldBe(["PATH"]);
    }

    [Fact]
    public void An_agents_shell_loses_what_Fleet_set_for_its_server_but_keeps_Fleets_URL()
    {
        // What Fleet starts an OpenCode 2 server with in separate mode, with a profile and messages between sessions.
        string[] setForServer =
        [
            "OPENCODE_CONFIG_DIR", "OPENCODE_DB", "OPENCODE_CONFIG", "OPENCODE_CONFIG_CONTENT", "OPENCODE_SERVER_PASSWORD",
            "FLEET_BRIDGE_TOKEN", "FLEET_SESSION_MESSAGES", "FLEET_SHELL_ENVIRONMENT", "FLEET_URL", "XDG_DATA_HOME",
        ];

        var changes = TerminalEnvironment.AgentShellChanges(new Hashtable { ["HOME"] = "/home/me" }, setForServer);

        changes.Keys.Order(StringComparer.Ordinal).ShouldBe(
        [
            "FLEET_BRIDGE_TOKEN", "FLEET_SESSION_MESSAGES", "FLEET_SHELL_ENVIRONMENT", "OPENCODE_CONFIG", "OPENCODE_CONFIG_CONTENT",
            "OPENCODE_CONFIG_DIR", "OPENCODE_DB", "OPENCODE_SERVER_PASSWORD",
        ]);
        changes.Values.ShouldAllBe(value => value == null);
    }

    [Fact]
    public void A_users_own_OpenCode_settings_come_back_in_the_agents_shell()
    {
        // The user points their own OpenCode at another database; Fleet gives its server one of its own.
        var inherited = new Hashtable { ["OPENCODE_DB"] = "/home/me/mine.db" };

        var changes = TerminalEnvironment.AgentShellChanges(inherited, ["OPENCODE_DB", "OPENCODE_CONFIG_DIR"]);

        changes["OPENCODE_DB"].ShouldBe("/home/me/mine.db");
        changes["OPENCODE_CONFIG_DIR"].ShouldBeNull();
    }
}
