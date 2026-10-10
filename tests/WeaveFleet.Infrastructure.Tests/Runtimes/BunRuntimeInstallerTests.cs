using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunRuntimeInstallerTests : IDisposable
{
    private const string Script = "#!/bin/sh\necho 1.4.2\n";

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fleet-bun-{Guid.NewGuid():N}");
    private readonly BunReleaseServer _server = new();

    public BunRuntimeInstallerTests() => Directory.CreateDirectory(_home);

    private string Root => Path.Combine(_home, ".weave", "runtimes", "bun");

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    public static TheoryData<string> Rids => new() { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64" };

    [Theory]
    [MemberData(nameof(Rids))]
    public async Task Installs_the_pinned_bun_for_each_platform(string rid)
    {
        var release = Publish();
        var asset = release.AssetFor(rid)!;
        var installer = NewInstaller(release, rid);

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        var executable = Path.Combine(Root, "1.4.2", asset.ExecutableName);
        result.Value.ShouldBe(new BunLocation(executable, BunSources.Installed, "1.4.2"));
        File.Exists(executable).ShouldBeTrue();
        File.Exists(Path.Combine(Root, "1.4.2", "install.json")).ShouldBeTrue();
        _server.Requests.ShouldBe([$"/bun-v1.4.2/{asset.FileName}"]);
        installer.Find().ShouldBe(new BunLocation(executable, BunSources.Installed, "1.4.2"));
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_installed_bun_is_executable_and_runs_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var release = Publish();
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        var path = result.Value.ExecutablePath;
        File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        output.Trim().ShouldBe("1.4.2");
    }

    [Fact]
    public async Task Refuses_a_download_that_does_not_match_its_checksum()
    {
        var release = Publish();
        var wrong = release with { Assets = [.. release.Assets.Select(a => a with { Sha256 = new string('0', 64) })] };
        var installer = NewInstaller(wrong, "linux-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe("The Bun 1.4.2 download didn't match its checksum, so Fleet deleted it.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Message.ShouldBe(result.Error.Description);
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeFalse();
        Leftovers().ShouldBeEmpty();
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Reports_a_download_that_stops_short()
    {
        var release = Publish(mode: BunServeMode.Truncate);
        var installer = NewInstaller(release, "linux-x64");
        var total = Zip("linux-x64").Length + 100;

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Description.ShouldStartWith("Couldn't download Bun 1.4.2: the download stopped after ");
        result.Error.Description.ShouldEndWith($" of {total} bytes.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        Everything().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(500, "Internal Server Error")]
    [InlineData(404, "Not Found")]
    public async Task Reports_the_status_when_the_server_will_not_send_the_archive(int status, string reason)
    {
        var release = Publish(status: status);
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Description.ShouldBe($"Couldn't download Bun 1.4.2: the server answered {status} {reason}.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gives_up_on_a_download_that_stalls()
    {
        var release = Publish(mode: BunServeMode.Stall);
        var installer = NewInstaller(release, "linux-x64", stall: TimeSpan.FromMilliseconds(300));

        var result = await installer.EnsureAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        result.Error.Description.ShouldBe("Couldn't download Bun 1.4.2: nothing arrived for 0.3 seconds.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_stops_the_download_cleans_up_and_a_later_call_installs()
    {
        var release = Publish(mode: BunServeMode.Stall);
        var installer = NewInstaller(release, "linux-x64");
        using var cts = new CancellationTokenSource();

        var install = installer.EnsureAsync(null, cts.Token);
        await _server.Stalled.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var error = await Record.ExceptionAsync(() => install.WaitAsync(TimeSpan.FromSeconds(20)));

        error.ShouldBeAssignableTo<OperationCanceledException>();
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Message.ShouldBe("The install was cancelled.");
        Everything().ShouldBeEmpty();

        Publish();
        var again = await installer.EnsureAsync(null, CancellationToken.None);

        again.IsSuccess.ShouldBeTrue();
        installer.Job.Phase.ShouldBe(BunInstallPhases.Succeeded);
    }

    [Fact]
    public async Task Reuses_an_install_that_is_already_there()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64");

        await installer.EnsureAsync(null, CancellationToken.None);
        var second = await installer.EnsureAsync(null, CancellationToken.None);

        second.Value.Source.ShouldBe(BunSources.Installed);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_an_archive_with_an_entry_outside_its_folder()
    {
        var release = Publish(build: asset => MakeZip(asset, extraEntry: "../evil.txt"));
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Description.ShouldStartWith("The Bun 1.4.2 archive has an entry Fleet won't unpack: ");
        File.Exists(Path.Combine(Root, "evil.txt")).ShouldBeFalse();
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeFalse();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_an_archive_without_the_executable()
    {
        var release = Publish(build: asset => MakeZip(asset, includeExecutable: false));
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Description.ShouldBe("The Bun 1.4.2 archive has no bun-linux-x64-baseline/bun.");
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeFalse();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_path_that_exists_wins_over_an_installed_bun()
    {
        var release = Publish();
        await NewInstaller(release, "linux-x64").EnsureAsync(null, CancellationToken.None);
        var configured = Path.Combine(_home, "my-bun");
        await File.WriteAllTextAsync(configured, Script);
        var installer = NewInstaller(release, "linux-x64", bunPath: configured);

        installer.Find().ShouldBe(new BunLocation(configured, BunSources.Configured, null));
        (await installer.EnsureAsync(null, CancellationToken.None)).Value.Source.ShouldBe(BunSources.Configured);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_configured_path_that_is_missing_is_an_error_and_nothing_downloads()
    {
        var release = Publish();
        var missing = Path.Combine(_home, "no-such-bun");
        var installer = NewInstaller(release, "linux-x64", bunPath: missing);

        installer.Find().ShouldBeNull();
        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe($"Fleet:Harness:BunPath is {missing}, which doesn't exist.");
        _server.Requests.ShouldBeEmpty();
        installer.Job.ShouldBeNull();
    }

    [Fact]
    public async Task Development_uses_bun_on_path_when_nothing_is_installed()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64", environment: Environments.Development, onPath: () => "/opt/bun/bin/bun");

        installer.Find().ShouldBe(new BunLocation("/opt/bun/bin/bun", BunSources.Path, null));
        (await installer.EnsureAsync(null, CancellationToken.None)).Value.Source.ShouldBe(BunSources.Path);
        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Production_ignores_bun_on_path_and_installs()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64", onPath: () => "/opt/bun/bin/bun");

        installer.Find().ShouldBeNull();
        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Value.Source.ShouldBe(BunSources.Installed);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Development_prefers_the_installed_bun_over_bun_on_path()
    {
        var release = Publish();
        await NewInstaller(release, "linux-x64").EnsureAsync(null, CancellationToken.None);
        var installer = NewInstaller(release, "linux-x64", environment: Environments.Development, onPath: () => "/opt/bun/bin/bun");

        installer.Find()!.Source.ShouldBe(BunSources.Installed);
    }

    [Fact]
    public async Task Reports_each_phase_in_order_and_keeps_the_job()
    {
        var release = Publish();
        var progress = new Recorder();
        var installer = NewInstaller(release, "linux-x64");
        var size = Zip("linux-x64").Length;

        await installer.EnsureAsync(progress, CancellationToken.None);

        var phases = progress.Events.Select(e => e.Phase).Distinct().ToList();
        phases.ShouldBe([BunInstallPhases.Downloading, BunInstallPhases.Verifying, BunInstallPhases.Extracting, BunInstallPhases.Succeeded]);
        progress.Events[0].ShouldBe(new BunInstallJob(BunInstallPhases.Downloading, "1.4.2", "Installing the mod runtime…", 0, null));
        var downloading = progress.Events.Where(e => e.Phase == BunInstallPhases.Downloading).ToList();
        downloading.Skip(1).First().BytesTotal.ShouldBe(size);
        downloading[^1].BytesReceived.ShouldBe(size);
        progress.Events.Select(e => e.BytesReceived).ShouldBe(progress.Events.Select(e => e.BytesReceived).Order());
        progress.Events[^1].Message.ShouldBe("Installed Bun 1.4.2.");
        installer.Job.ShouldBe(progress.Events[^1]);
    }

    [Fact]
    public async Task Two_callers_at_once_download_once()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64");

        var results = await Task.WhenAll(
            installer.EnsureAsync(null, CancellationToken.None),
            installer.EnsureAsync(null, CancellationToken.None));

        results.ShouldAllBe(r => r.IsSuccess);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Replaces_a_version_folder_that_is_not_a_finished_install()
    {
        var release = Publish();
        var stale = Path.Combine(Root, "1.4.2");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "junk.txt"), "half an install");
        var installer = NewInstaller(release, "linux-x64");

        installer.Find().ShouldBeNull();
        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Value.Source.ShouldBe(BunSources.Installed);
        File.Exists(Path.Combine(stale, "junk.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(stale, "install.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Has_no_download_for_a_platform_the_release_lacks()
    {
        var release = Publish();
        var installer = NewInstaller(release, "freebsd-x64");

        var result = await installer.EnsureAsync(null, CancellationToken.None);

        result.Error.Description.ShouldBe("Fleet has no Bun build for freebsd-x64. Install Bun and set Fleet:Harness:BunPath to it.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deletes_leftovers_over_an_hour_old_and_keeps_fresh_ones()
    {
        var release = Publish();
        var old = Path.Combine(Root, ".staging-old");
        var oldDownload = Path.Combine(Root, ".download-old");
        var fresh = Path.Combine(Root, ".staging-fresh");
        foreach (var directory in new[] { old, oldDownload, fresh })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "partial"), "x");
        }

        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(oldDownload, DateTime.UtcNow.AddHours(-2));
        var installer = NewInstaller(release, "linux-x64");

        await installer.EnsureAsync(null, CancellationToken.None);

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(oldDownload).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
    }

    // -- helpers ------------------------------------------------------------------------------------------------

    private BunRuntimeInstaller NewInstaller(
        BunRelease release,
        string rid,
        string environment = "Production",
        string? bunPath = null,
        Func<string?>? onPath = null,
        TimeSpan? stall = null) =>
        new(
            new FleetOptions { Harness = { BunPath = bunPath ?? "" } },
            new FakeEnvironment(environment),
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = _home,
            Release = release,
            Rid = rid,
            DownloadBase = _server.BaseUri,
            FindOnPath = onPath ?? (() => null),
            StallTimeout = stall ?? TimeSpan.FromSeconds(60),
        };

    /// <summary>Serves a fake archive for every pinned platform and returns a release whose checksums match them.</summary>
    private BunRelease Publish(
        int status = 200,
        BunServeMode mode = BunServeMode.Normal,
        Func<BunAsset, byte[]>? build = null)
    {
        var assets = new List<BunAsset>();
        foreach (var pinned in BunRelease.Pinned.Assets)
        {
            var bytes = build is null ? Zip(pinned.Rid) : build(pinned);
            _server.Serve($"/bun-v1.4.2/{pinned.FileName}", bytes, status, mode);
            assets.Add(pinned with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }

        return new BunRelease("1.4.2", assets);
    }

    private static byte[] Zip(string rid) => MakeZip(BunRelease.Pinned.AssetFor(rid)!);

    private static byte[] MakeZip(BunAsset asset, bool includeExecutable = true, string? extraEntry = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry($"{asset.Folder}/");
            if (includeExecutable)
            {
                var entry = zip.CreateEntry($"{asset.Folder}/{asset.ExecutableName}");
                entry.ExternalAttributes = 0x81A4 << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(Script);
            }

            if (extraEntry is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry(extraEntry).Open());
                writer.Write("not yours");
            }
        }

        return stream.ToArray();
    }

    /// <summary>Folders an install left behind that it should have deleted.</summary>
    private List<string> Leftovers() => Directory.Exists(Root)
        ? [.. Directory.GetDirectories(Root).Where(d =>
            Path.GetFileName(d).StartsWith(".download-", StringComparison.Ordinal)
            || Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal))]
        : [];

    /// <summary>Everything under the runtime root; a failed install leaves it empty (the root itself may remain).</summary>
    private List<string> Everything() => Directory.Exists(Root)
        ? [.. Directory.GetFileSystemEntries(Root)]
        : [];

    private sealed class Recorder : IProgress<BunInstallJob>
    {
        public List<BunInstallJob> Events { get; } = [];

        public void Report(BunInstallJob value) => Events.Add(value);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = Environment.CurrentDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
