using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>Finding the mod host's script, installed or in the repository, and Fleet's version.</summary>
public sealed class ModHostFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-modhost-{Guid.NewGuid():N}");

    public ModHostFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that can't be deleted right now is the OS's to clean up.
        }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// host");
        return path;
    }

    [Fact]
    public void Finds_the_script_in_the_installed_layout()
    {
        var script = Touch("app", "mods-host", "host.js");

        new ModHostFiles(Path.Combine(_root, "app")).HostScript.ShouldBe(script);
    }

    [Fact]
    public void Finds_the_script_in_the_repository_by_walking_up()
    {
        Touch("repo", "WeaveFleet.slnx");
        var script = Touch("repo", "mods", "host", "dist", "host.js");
        var bin = Path.Combine(_root, "repo", "src", "WeaveFleet.Api", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);

        new ModHostFiles(bin).HostScript.ShouldBe(script);
    }

    [Fact]
    public void Ignores_a_built_host_when_the_solution_file_is_not_beside_it()
    {
        Touch("repo", "mods", "host", "dist", "host.js");
        var bin = Path.Combine(_root, "repo", "bin");
        Directory.CreateDirectory(bin);

        new ModHostFiles(bin).HostScript.ShouldBeNull();
    }

    [Fact]
    public void The_installed_script_wins_over_the_repository_one()
    {
        Touch("repo", "WeaveFleet.slnx");
        Touch("repo", "mods", "host", "dist", "host.js");
        var installed = Touch("repo", "app", "mods-host", "host.js");

        new ModHostFiles(Path.Combine(_root, "repo", "app")).HostScript.ShouldBe(installed);
    }

    [Fact]
    public void The_override_wins_when_the_file_exists()
    {
        Touch("app", "mods-host", "host.js");
        var custom = Touch("custom", "my-host.js");

        new ModHostFiles(Path.Combine(_root, "app"), custom).HostScript.ShouldBe(custom);
    }

    [Fact]
    public void A_missing_override_falls_back_to_the_installed_script()
    {
        var installed = Touch("app", "mods-host", "host.js");

        new ModHostFiles(Path.Combine(_root, "app"), Path.Combine(_root, "nope.js")).HostScript.ShouldBe(installed);
    }

    [Fact]
    public void Returns_null_until_the_script_exists_and_then_finds_it()
    {
        var files = new ModHostFiles(Path.Combine(_root, "app"));
        files.HostScript.ShouldBeNull();

        var script = Touch("app", "mods-host", "host.js");

        files.HostScript.ShouldBe(script);
    }

    [Fact]
    public void Stops_walking_after_a_few_levels()
    {
        Touch("WeaveFleet.slnx");
        Touch("mods", "host", "dist", "host.js");
        var deep = Path.Combine(_root, "a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        Directory.CreateDirectory(deep);

        new ModHostFiles(deep).HostScript.ShouldBeNull();
    }

    [Fact]
    public void The_version_has_no_commit_suffix()
    {
        var version = new ModHostFiles(_root).FleetVersion;

        version.ShouldNotBeNullOrWhiteSpace();
        version.ShouldNotContain('+');
    }
}
