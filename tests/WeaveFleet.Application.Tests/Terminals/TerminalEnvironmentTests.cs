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
}
