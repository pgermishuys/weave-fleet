using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>Finding the mod host's script, installed or in the repository.</summary>
public sealed class ModHostFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-modhost-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// host");
        return path;
    }

    [Fact]
    public void Finds_the_installed_script()
    {
        var script = Touch("app", "mods-host", "host.js");

        new ModHostFiles(Path.Combine(_root, "app")).HostScript.ShouldBe(script);
    }

    [Fact]
    public void Finds_the_repository_script_by_walking_up_to_the_solution_folder()
    {
        Touch("repo", "WeaveFleet.slnx");
        var script = Touch("repo", "mods", "host", "dist", "host.js");
        var bin = Path.Combine(_root, "repo", "src", "Api", "bin");
        Directory.CreateDirectory(bin);

        new ModHostFiles(bin).HostScript.ShouldBe(script);
    }

    [Fact]
    public void The_override_wins_when_it_exists()
    {
        Touch("app", "mods-host", "host.js");
        var custom = Touch("custom", "host.js");

        new ModHostFiles(Path.Combine(_root, "app"), custom).HostScript.ShouldBe(custom);
    }

    [Fact]
    public void Is_null_until_the_script_exists_then_found()
    {
        var files = new ModHostFiles(Path.Combine(_root, "app"));
        files.HostScript.ShouldBeNull();

        var script = Touch("app", "mods-host", "host.js");

        files.HostScript.ShouldBe(script);
        files.FleetVersion.ShouldNotContain('+');
    }
}
