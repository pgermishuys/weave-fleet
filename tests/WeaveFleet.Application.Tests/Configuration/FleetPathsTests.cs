using Shouldly;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

[Collection(FleetPathsEnvironmentTests.Name)]
public sealed class FleetPathsTests
{
    [Fact]
    public void Should_root_the_app_data_directory_when_the_platform_folder_does_not_exist_yet()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A fresh HOME (a new user, a scratch HOME, a CI runner) has no ~/.local/share yet. Fleet must still write
        // there, not to ./WeaveFleet under whatever folder it was started from.
        var missing = Path.Combine(Path.GetTempPath(), "fleet-paths-" + Guid.NewGuid().ToString("N"), "share");
        using var _ = new EnvironmentVariableScope("XDG_DATA_HOME", missing);

        var directory = FleetPaths.ResolveDefaultAppDataDirectory();

        Path.IsPathRooted(directory).ShouldBeTrue(directory);
        directory.ShouldBe(Path.Combine(missing, "WeaveFleet"));
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FleetPathsEnvironmentTests
{
    public const string Name = "FleetPathsEnvironment";
}
