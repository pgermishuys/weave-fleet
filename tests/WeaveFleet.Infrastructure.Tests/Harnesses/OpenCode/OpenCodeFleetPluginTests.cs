using System.Text.Json;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeFleetPluginTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fleet-plugin-{Guid.NewGuid():N}");

    private string PluginPath => Path.Combine(_dataDirectory, "opencode", OpenCodeFleetPlugin.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public void Install_writes_the_repo_plugin_and_returns_its_file_uri()
    {
        var uri = OpenCodeFleetPlugin.Install(_dataDirectory);

        uri.ShouldBe(new Uri(PluginPath).AbsoluteUri);
        uri.ShouldStartWith("file:///");
        File.ReadAllText(PluginPath).ShouldBe(File.ReadAllText(RepoPluginPath()));
        Directory.GetFiles(Path.GetDirectoryName(PluginPath)!).ShouldBe([PluginPath]);
    }

    [Fact]
    public void Install_leaves_an_identical_copy_alone()
    {
        OpenCodeFleetPlugin.Install(_dataDirectory);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(PluginPath, written);

        OpenCodeFleetPlugin.Install(_dataDirectory);

        File.GetLastWriteTimeUtc(PluginPath).ShouldBe(written);
    }

    [Fact]
    public void Install_replaces_a_copy_that_differs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PluginPath)!);
        File.WriteAllText(PluginPath, "// an older plugin");

        OpenCodeFleetPlugin.Install(_dataDirectory);

        File.ReadAllBytes(PluginPath).ShouldBe(OpenCodeFleetPlugin.ReadEmbedded());
    }

    [Fact]
    public void Config_content_asks_for_everything_but_reading()
    {
        // Fleet answers each ask for the session's permission level, so a subagent, which gets this config rather than
        // its parent's session rules, asks as its parent does. OpenCode applies the last rule that matches.
        using var config = JsonDocument.Parse(OpenCodeProcessManager.BuildConfigContent([]));
        var rules = config.RootElement.GetProperty("permission").EnumerateObject().Select(rule => (rule.Name, rule.Value.GetString())).ToList();

        rules[0].ShouldBe(("*", "ask"));
        rules.Skip(1).ShouldAllBe(rule => rule.Item2 == "allow");
        rules.Select(rule => rule.Name).ShouldContain("read");
        rules.Select(rule => rule.Name).ShouldContain("fleet_*");
        rules.Select(rule => rule.Name).ShouldNotContain("bash");
        rules.Select(rule => rule.Name).ShouldNotContain("edit");
        config.RootElement.TryGetProperty("plugin", out _).ShouldBeFalse();
    }

    [Fact]
    public void Config_content_lists_the_plugins()
    {
        using var config = JsonDocument.Parse(OpenCodeProcessManager.BuildConfigContent(["file:///home/u/.weave/opencode/fleet-canvas.ts"]));

        config.RootElement.GetProperty("plugin").EnumerateArray().Select(plugin => plugin.GetString())
            .ShouldBe(["file:///home/u/.weave/opencode/fleet-canvas.ts"]);
    }

    private static string RepoPluginPath([System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFile)!);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "opencode", "fleet")))
            directory = directory.Parent;

        return Path.Combine(directory!.FullName, "opencode", "fleet", OpenCodeFleetPlugin.FileName);
    }
}
