using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

/// <summary>Running mods on a Bun the user picked from the ones Fleet found: the pointer file and where it ranks.</summary>
public sealed class BunRuntimeUseTests : IDisposable
{
    private static readonly BunRelease Release = BunRelease.Pinned;

    /// <summary>The finder searches the way this host does: bun.exe and a <c>;</c>-separated PATH on Windows.</summary>
    private static readonly bool Host = OperatingSystem.IsWindows();

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fleet-bun-use-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _versions = new();
    private string? _unsafeReason;

    public BunRuntimeUseTests() => Directory.CreateDirectory(Path.Combine(_home, "bin"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Root => Path.Combine(_home, ".weave", "runtimes", "bun");

    private string Pointer => Path.Combine(Root, "use.json");

    /// <summary>The path in the pointer file, read as JSON: a Windows path is written with its backslashes escaped.</summary>
    private string? PointedPath()
    {
        using var pointer = JsonDocument.Parse(File.ReadAllText(Pointer));
        return pointer.RootElement.GetProperty("path").GetString();
    }

    /// <summary>A bun on the fake <c>PATH</c> that prints <paramref name="version"/>.</summary>
    private string PutBun(string version, string folder = "bin")
    {
        var directory = Path.Combine(_home, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Host ? "bun.exe" : "bun");
        File.WriteAllText(path, $"#!/bin/sh\necho {version}\n");
        _versions[path] = version;
        return path;
    }

    private BunRuntimeInstaller NewInstaller(string bunPath = "", string[]? onPath = null)
    {
        Task<BunProbeResult> Probe(string path, CancellationToken ct) => Task.FromResult(
            _versions.TryGetValue(path, out var version) ? new BunProbeResult(BunVersion.Parse(version), null) : new BunProbeResult(null, "it didn't run"));

        return new BunRuntimeInstaller(
            new FleetOptions { Harness = { BunPath = bunPath } },
            new FakeHttpClientFactory(),
            new BunRuntimeInstallerTests.FixedReleases(BunRelease.Pinned),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = _home,
            Rid = "linux-x64",
            IsWindows = Host,
            Probe = Probe,
            Finder = new BunMachineFinder
            {
                Home = _home,
                PathEnv = string.Join(Path.PathSeparator, (onPath ?? ["bin"]).Select(folder => Path.Combine(_home, folder))),
                SystemDirectories = [],
                IsWindows = Host,
                Probe = Probe,
                WhyNotSafeToRun = _ => _unsafeReason,
            },
        };
    }

    private void PlantInstalled()
    {
        var folder = Path.Combine(Root, Release.Version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "install.json"),
            $"{{ \"version\": \"{Release.Version}\", \"rid\": \"linux-x64\", \"assetFileName\": \"bun.zip\", " +
            $"\"sha256\": \"{Release.AssetFor("linux-x64")!.Sha256}\", \"installedAt\": \"2026-10-01T00:00:00+00:00\" }}");
        File.WriteAllText(Path.Combine(folder, "bun"), "#!/bin/sh\necho 1.4.2\n");
    }

    [Fact]
    public async Task Using_a_found_bun_saves_a_pointer_and_find_answers_with_it_as_the_users_own()
    {
        var mine = PutBun("1.4.5");
        var installer = NewInstaller();

        var result = await installer.UseAsync(mine, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        result.Value.ShouldBe(new BunLocation(mine, BunSources.Configured, "1.4.5"));
        PointedPath().ShouldBe(mine);
        (await installer.FindAsync(Release, CancellationToken.None)).ShouldBe(new BunLocation(mine, BunSources.Configured, "1.4.5"));
    }

    [Fact]
    public async Task The_choice_outlives_the_installer_because_it_is_a_file()
    {
        var mine = PutBun("1.4.5");
        await NewInstaller().UseAsync(mine, CancellationToken.None);

        var afterRestart = NewInstaller();

        (await afterRestart.FindAsync(Release, CancellationToken.None))!.ExecutablePath.ShouldBe(mine);
    }

    [Fact]
    public async Task The_pointer_ranks_before_fleets_own_bun_and_ensure_downloads_nothing()
    {
        var mine = PutBun("1.4.5");
        PlantInstalled();
        var installer = NewInstaller();
        (await installer.FindAsync(Release, CancellationToken.None))!.Source.ShouldBe(BunSources.Installed);
        await installer.UseAsync(mine, CancellationToken.None);

        (await installer.FindAsync(Release, CancellationToken.None))!.ExecutablePath.ShouldBe(mine);
        var ensured = await installer.EnsureAsync(Release, null, CancellationToken.None);

        ensured.Value.ExecutablePath.ShouldBe(mine);
        installer.Job.ShouldBeNull();
    }

    [Fact]
    public async Task A_bun_set_in_the_configuration_ranks_before_the_pointer()
    {
        var mine = PutBun("1.4.5");
        var configured = PutBun("1.4.7", "configured");
        await NewInstaller().UseAsync(mine, CancellationToken.None);

        var found = await NewInstaller(bunPath: configured).FindAsync(Release, CancellationToken.None);

        found.ShouldBe(new BunLocation(configured, BunSources.Configured, "1.4.7"));
    }

    [Fact]
    public async Task A_path_the_finder_did_not_list_is_refused_and_nothing_is_saved()
    {
        PutBun("1.4.5");
        var elsewhere = PutBun("1.4.6", "elsewhere");

        var result = await NewInstaller().UseAsync(elsewhere, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain(elsewhere);
        File.Exists(Pointer).ShouldBeFalse();
    }

    [Fact]
    public async Task A_found_bun_that_is_too_old_is_refused_with_the_finders_reason()
    {
        var old = PutBun("1.3.0");

        var result = await NewInstaller().UseAsync(old, CancellationToken.None);

        result.Error.Description.ShouldContain("older than 1.4.0");
        File.Exists(Pointer).ShouldBeFalse();
    }

    [Fact]
    public async Task A_found_bun_that_is_not_safe_to_run_is_refused()
    {
        if (OperatingSystem.IsWindows()) return; // Fleet checks who can change a Bun on Linux and macOS only.

        var mine = PutBun("1.4.5");
        _unsafeReason = "Others can change it.";

        var result = await NewInstaller().UseAsync(mine, CancellationToken.None);

        result.Error.Description.ShouldBe("Others can change it.");
        File.Exists(Pointer).ShouldBeFalse();
    }

    [Fact]
    public async Task A_pointed_bun_that_is_gone_is_ignored_and_fleet_falls_back_to_its_own()
    {
        var mine = PutBun("1.4.5");
        PlantInstalled();
        var installer = NewInstaller();
        await installer.UseAsync(mine, CancellationToken.None);
        File.Delete(mine);

        var found = await installer.FindAsync(Release, CancellationToken.None);
        var again = await installer.FindAsync(Release, CancellationToken.None);

        found!.Source.ShouldBe(BunSources.Installed);
        again.ShouldBe(found);
    }

    [Fact]
    public async Task A_pointed_bun_that_is_gone_with_nothing_installed_finds_none()
    {
        var mine = PutBun("1.4.5");
        var installer = NewInstaller();
        await installer.UseAsync(mine, CancellationToken.None);
        File.Delete(mine);

        (await installer.FindAsync(Release, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_pointed_bun_that_became_unsafe_is_ignored_at_the_next_resolve()
    {
        if (OperatingSystem.IsWindows()) return; // Fleet checks who can change a Bun on Linux and macOS only.

        var mine = PutBun("1.4.5");
        PlantInstalled();
        var installer = NewInstaller();
        await installer.UseAsync(mine, CancellationToken.None);
        (await installer.FindAsync(Release, CancellationToken.None))!.ExecutablePath.ShouldBe(mine);

        _unsafeReason = "Others can change it.";

        (await installer.FindAsync(Release, CancellationToken.None))!.Source.ShouldBe(BunSources.Installed);
    }

    [Fact]
    public async Task A_pointed_bun_replaced_by_an_older_one_is_ignored()
    {
        var mine = PutBun("1.4.5");
        PlantInstalled();
        var installer = NewInstaller();
        await installer.UseAsync(mine, CancellationToken.None);

        _versions[mine] = "1.3.0";
        File.WriteAllText(mine, "#!/bin/sh\necho 1.3.0 and more\n");

        (await installer.FindAsync(Release, CancellationToken.None))!.Source.ShouldBe(BunSources.Installed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"path\": \"relative/bun\" }")]
    public async Task A_pointer_file_that_is_damaged_never_crashes(string content)
    {
        PlantInstalled();
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Pointer, content);
        var installer = NewInstaller();

        (await installer.FindAsync(Release, CancellationToken.None))!.Source.ShouldBe(BunSources.Installed);
        (await installer.EnsureAsync(Release, null, CancellationToken.None)).Value.Source.ShouldBe(BunSources.Installed);
    }

    [Fact]
    public async Task Choosing_again_replaces_the_pointer_and_leaves_no_temporary_file()
    {
        var first = PutBun("1.4.5");
        var second = PutBun("1.4.6", "bin2");
        var installer = NewInstaller();
        await installer.UseAsync(first, CancellationToken.None);
        var both = NewInstaller(onPath: ["bin2", "bin"]);
        (await both.UseAsync(second, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        PointedPath().ShouldBe(second);
        Directory.GetFiles(Root).Select(Path.GetFileName).ShouldBe(["use.json"]);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("Nothing here downloads.");
    }
}
