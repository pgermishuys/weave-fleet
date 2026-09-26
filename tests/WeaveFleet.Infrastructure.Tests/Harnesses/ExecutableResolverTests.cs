using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-resolver-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_an_executable_on_PATH()
    {
        var onPath = Folder("on-path");
        var expected = Executable(onPath, "tool");

        ExecutableResolver.TryResolve("tool", onPath, [], out var path).ShouldBeTrue();
        path.ShouldBe(expected);
    }

    [Fact]
    public void Finds_an_executable_in_an_install_folder_that_is_not_on_PATH()
    {
        // An installer added ~/.opencode/bin to the shell profile, but Fleet's PATH predates it.
        var installFolder = Folder("install");
        var expected = Executable(installFolder, "tool");

        ExecutableResolver.TryResolve("tool", Folder("empty"), [installFolder], out var path).ShouldBeTrue();
        path.ShouldBe(expected);
    }

    [Fact]
    public void Prefers_PATH_over_an_install_folder()
    {
        var onPath = Folder("on-path");
        var installFolder = Folder("install");
        var expected = Executable(onPath, "tool");
        Executable(installFolder, "tool");

        ExecutableResolver.TryResolve("tool", onPath, [installFolder], out var path).ShouldBeTrue();
        path.ShouldBe(expected);
    }

    [Fact]
    public void Reports_not_found_and_keeps_the_name()
    {
        ExecutableResolver.TryResolve("tool", Folder("empty"), [Folder("install")], out var path).ShouldBeFalse();
        path.ShouldBe("tool");
    }

    [Fact]
    public void Takes_a_configured_path_as_is_when_the_file_exists()
    {
        var configured = Executable(Folder("custom"), "tool");

        ExecutableResolver.TryResolve(configured, null, [], out var path).ShouldBeTrue();
        path.ShouldBe(configured);
        ExecutableResolver.TryResolve(Path.Combine(_root, "missing", "tool"), null, [], out _).ShouldBeFalse();
    }

    [Fact]
    public void Finds_every_copy_in_order_once_each()
    {
        // opencode can be OpenCode 1 in one folder and OpenCode 2 in another; the caller runs them to tell.
        var onPath = Folder("on-path");
        var installFolder = Folder("install");
        var first = Executable(onPath, "tool");
        var second = Executable(installFolder, "tool");
        Folder("empty");

        ExecutableResolver.FindAll("tool", $"{onPath}{Path.PathSeparator}{Path.Combine(_root, "empty")}", [installFolder, onPath])
            .ShouldBe([first, second]);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Executable(string folder, string name)
    {
        // On Windows the resolver only accepts PATHEXT extensions.
        var path = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
