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
    public void Config_content_without_plugins_only_allows_permissions()
    {
        OpenCodeProcessManager.BuildConfigContent([]).ShouldBe("""{"permission":{"*":"allow"}}""");
    }

    [Fact]
    public void Config_content_lists_the_plugins()
    {
        OpenCodeProcessManager.BuildConfigContent(["file:///home/u/.weave/opencode/fleet-canvas.ts"])
            .ShouldBe("""{"permission":{"*":"allow"},"plugin":["file:///home/u/.weave/opencode/fleet-canvas.ts"]}""");
    }

    private static string RepoPluginPath([System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFile)!);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "opencode", "fleet")))
            directory = directory.Parent;

        return Path.Combine(directory!.FullName, "opencode", "fleet", OpenCodeFleetPlugin.FileName);
    }
}
