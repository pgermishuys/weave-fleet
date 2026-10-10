using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunRuntimeInstallerTests : IDisposable
{
    private static string Script(string version) => $"#!/bin/sh\necho {version}\n";

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

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        var executable = Path.Combine(Root, "1.4.2", asset.ExecutableName);
        result.Value.ShouldBe(new BunLocation(executable, BunSources.Installed, "1.4.2"));
        File.Exists(executable).ShouldBeTrue();
        File.Exists(Path.Combine(Root, "1.4.2", "install.json")).ShouldBeTrue();
        _server.Requests.ShouldBe([$"/bun-v1.4.2/{asset.FileName}"]);
        (await installer.FindAsync(release, CancellationToken.None)).ShouldBe(new BunLocation(executable, BunSources.Installed, "1.4.2"));
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_installed_bun_is_executable_and_runs_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var release = Publish();
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

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

        var result = await installer.EnsureAsync(wrong, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe(
            "The download didn't match Bun 1.4.2's checksum, so Fleet deleted it. " +
            "Try again; if it happens again, something between you and GitHub is changing files.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Checksum);
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

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe("The download stopped after 0 of 0 MB. Try again.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Stopped);
        Everything().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(500, "Internal Server Error")]
    [InlineData(404, "Not Found")]
    public async Task Reports_the_status_when_the_server_will_not_send_the_archive(int status, string reason)
    {
        var release = Publish(status: status);
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe($"127.0.0.1 answered {status} {reason}.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Other);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Says_a_proxy_or_firewall_is_probably_blocking_when_the_server_refuses()
    {
        const int status = 403;
        const string reason = "Forbidden";
        var release = Publish(status: status);
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe(
            $"127.0.0.1 answered {status} {reason}, so a proxy or firewall is probably blocking downloads from GitHub.");
        installer.Job!.Reason.ShouldBe(BunInstallFailures.Blocked);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Downloads_from_the_configured_download_base()
    {
        var release = Publish();
        var installer = new BunRuntimeInstaller(
            new FleetOptions { Harness = { BunDownloadBase = _server.BaseUri.ToString().TrimEnd('/') } },
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = _home,
            Rid = "linux-x64",
        };

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        _server.Requests.ShouldBe(["/bun-v1.4.2/bun-linux-x64-baseline.zip"]);
        installer.DownloadBase.ShouldBe(_server.BaseUri);
    }

    [Fact]
    public void An_invalid_download_base_falls_back_to_github()
    {
        var installer = new BunRuntimeInstaller(
            new FleetOptions { Harness = { BunDownloadBase = "ftp://nope" } },
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance);

        installer.DownloadBase.ShouldBe(BunRelease.GitHubDownloads);
    }

    [Fact]
    public async Task Says_the_connection_was_refused_when_nothing_listens()
    {
        var release = Publish();
        var port = FreePort();
        var installer = NewInstaller(release, "linux-x64", downloadBase: new Uri($"http://127.0.0.1:{port}/"));

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe(
            "Fleet couldn't reach 127.0.0.1: the connection was refused. Check that this computer is online, then try again.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Offline);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Says_the_connection_timed_out_when_nothing_arrives_before_the_first_byte()
    {
        var release = Publish(mode: BunServeMode.Silent);
        var installer = NewInstaller(release, "linux-x64", stall: TimeSpan.FromMilliseconds(20));

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe(
            "Fleet couldn't reach 127.0.0.1: the connection timed out. Check that this computer is online, then try again.");
        installer.Job!.Reason.ShouldBe(BunInstallFailures.Offline);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gives_up_on_a_download_that_stalls()
    {
        var release = Publish(mode: BunServeMode.Stall);
        var installer = NewInstaller(release, "linux-x64", stall: TimeSpan.FromMilliseconds(300));

        var result = await installer.EnsureAsync(release, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        result.Error.Description.ShouldBe("The download stopped after 0 of 0 MB. Try again.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Stopped);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_stops_the_download_cleans_up_and_a_later_call_installs()
    {
        var release = Publish(mode: BunServeMode.Stall);
        var installer = NewInstaller(release, "linux-x64");
        using var cts = new CancellationTokenSource();

        var install = installer.EnsureAsync(release, null, cts.Token);
        await _server.Stalled.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var error = await Record.ExceptionAsync(() => install.WaitAsync(TimeSpan.FromSeconds(20)));

        error.ShouldBeAssignableTo<OperationCanceledException>();
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Message.ShouldBe("The install was cancelled.");
        installer.Job.Reason.ShouldBe(BunInstallFailures.Cancelled);
        Everything().ShouldBeEmpty();

        Publish();
        var again = await installer.EnsureAsync(release, null, CancellationToken.None);

        again.IsSuccess.ShouldBeTrue();
        installer.Job.Phase.ShouldBe(BunInstallPhases.Succeeded);
    }

    [Fact]
    public async Task Reuses_an_install_that_is_already_there()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64");

        await installer.EnsureAsync(release, null, CancellationToken.None);
        var second = await installer.EnsureAsync(release, null, CancellationToken.None);

        second.Value.Source.ShouldBe(BunSources.Installed);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_an_archive_with_an_entry_outside_its_folder()
    {
        var release = Publish(build: asset => MakeZip(asset, extraEntry: "../evil.txt"));
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

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

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe("The Bun 1.4.2 archive has no bun-linux-x64-baseline/bun.");
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeFalse();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_path_that_exists_wins_over_an_installed_bun()
    {
        var release = Publish();
        await NewInstaller(release, "linux-x64").EnsureAsync(release, null, CancellationToken.None);
        var configured = Path.Combine(_home, "my-bun.exe");
        await File.WriteAllTextAsync(configured, Script("1.4.5"));
        var installer = NewInstaller(release, "linux-x64", bunPath: configured, probe: Prints("1.4.5"));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBe(new BunLocation(configured, BunSources.Configured, "1.4.5"));
        (await installer.EnsureAsync(release, null, CancellationToken.None)).Value.Source.ShouldBe(BunSources.Configured);
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_configured_path_that_is_missing_is_an_error_and_nothing_downloads()
    {
        var release = Publish();
        var missing = Path.Combine(_home, "no-such-bun.exe");
        var installer = NewInstaller(release, "linux-x64", bunPath: missing);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe($"Fleet:Harness:BunPath is {missing}, which doesn't exist.");
        _server.Requests.ShouldBeEmpty();
        installer.Job.ShouldBeNull();
    }

    [Fact]
    public async Task Never_looks_on_path_in_any_build_so_it_installs()
    {
        var release = Publish();
        var onPath = Path.Combine(_home, "path-bin");
        Directory.CreateDirectory(onPath);
        await File.WriteAllTextAsync(Path.Combine(onPath, "bun"), Script("1.4.9"));
        var probed = 0;
        var installer = NewInstaller(release, "linux-x64", probe: (_, _) => { probed++; return Task.FromResult(new BunProbeResult(BunVersion.Parse("1.4.9"), null)); });
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", onPath + Path.PathSeparator + path);
        try
        {
            (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
            var result = await installer.EnsureAsync(release, null, CancellationToken.None);

            result.Value.Source.ShouldBe(BunSources.Installed);
            probed.ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    [Fact]
    public async Task Reports_each_phase_in_order_and_keeps_the_job()
    {
        var release = Publish();
        var progress = new Recorder();
        var installer = NewInstaller(release, "linux-x64");
        var size = Zip("linux-x64").Length;

        await installer.EnsureAsync(release, progress, CancellationToken.None);

        var phases = new List<string>();
        foreach (var phase in progress.Events.Select(e => e.Phase))
        {
            if (phases.Count == 0 || phases[^1] != phase)
                phases.Add(phase);
        }

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
            installer.EnsureAsync(release, null, CancellationToken.None),
            installer.EnsureAsync(release, null, CancellationToken.None));

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

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Value.Source.ShouldBe(BunSources.Installed);
        File.Exists(Path.Combine(stale, "junk.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(stale, "install.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Has_no_download_for_a_platform_the_release_lacks()
    {
        var release = Publish();
        var installer = NewInstaller(release, "freebsd-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe("Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.");
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.NoBuild);
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

        foreach (var directory in new[] { old, oldDownload })
        {
            File.SetLastWriteTimeUtc(Path.Combine(directory, "partial"), DateTime.UtcNow.AddHours(-2));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddHours(-2));
        }

        var installer = NewInstaller(release, "linux-x64");

        await installer.EnsureAsync(release, null, CancellationToken.None);

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(oldDownload).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
    }

    [Fact]
    public async Task Retries_the_final_move_when_access_is_denied_for_a_moment()
    {
        var release = Publish();
        var attempts = 0;
        var installer = NewInstaller(release, "linux-x64", move: (from, to) =>
        {
            if (++attempts <= 2)
                throw new UnauthorizedAccessException("Access to the path is denied.");
            Directory.Move(from, to);
        });

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        attempts.ShouldBe(3);
        File.Exists(Path.Combine(Root, "1.4.2", "install.json")).ShouldBeTrue();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task Fails_with_a_clear_message_and_cleans_up_when_the_move_never_works()
    {
        var release = Publish();
        var attempts = 0;
        var installer = NewInstaller(release, "linux-x64", move: (_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("Access to the path is denied.");
        });

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe("Couldn't install Bun 1.4.2: Access to the path is denied.");
        attempts.ShouldBe(5);
        installer.Job!.Phase.ShouldBe(BunInstallPhases.Failed);
        installer.Job.Reason.ShouldBe(BunInstallFailures.Other);
        Everything().ShouldBeEmpty();
    }

    [Fact]
    public async Task Keeps_an_install_another_fleet_finished_while_the_move_was_failing()
    {
        var release = Publish();
        var asset = release.AssetFor("linux-x64")!;
        var installer = NewInstaller(release, "linux-x64", move: (from, to) =>
        {
            Directory.Move(from, to);
            throw new IOException("Looks like it failed, but it didn't.");
        });

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        File.Exists(Path.Combine(Root, "1.4.2", asset.ExecutableName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Keeps_a_download_folder_whose_files_are_still_being_written()
    {
        var release = Publish();
        var live = Path.Combine(Root, ".download-live");
        var dead = Path.Combine(Root, ".download-dead");
        foreach (var directory in new[] { live, dead })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "bun.zip"), "x");
        }

        var old = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(Path.Combine(dead, "bun.zip"), old);
        Directory.SetLastWriteTimeUtc(live, old);
        Directory.SetLastWriteTimeUtc(dead, old);
        var installer = NewInstaller(release, "linux-x64");

        await installer.EnsureAsync(release, null, CancellationToken.None);

        Directory.Exists(live).ShouldBeTrue();
        Directory.Exists(dead).ShouldBeFalse();
    }

    [Fact]
    public async Task Leaves_no_staging_folder_after_replacing_a_broken_version_folder()
    {
        var release = Publish();
        var broken = Path.Combine(Root, "1.4.2");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "junk.txt"), "half an install");
        var installer = NewInstaller(release, "linux-x64");

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        Directory.GetDirectories(Root, ".staging-*").ShouldBeEmpty();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_relative_configured_path_is_an_error_and_nothing_downloads()
    {
        var release = Publish();
        var installer = NewInstaller(release, "linux-x64", bunPath: "tools/bun");

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe("Fleet:Harness:BunPath must be an absolute path; it's tools/bun.");
        _server.Requests.ShouldBeEmpty();
        installer.Job.ShouldBeNull();
    }

    [Fact]
    public async Task Reinstalls_when_the_manifest_is_valid_but_the_binary_is_missing()
    {
        var release = Publish();
        var asset = release.AssetFor("linux-x64")!;
        var installer = NewInstaller(release, "linux-x64");
        await installer.EnsureAsync(release, null, CancellationToken.None);
        var executable = Path.Combine(Root, "1.4.2", asset.ExecutableName);
        File.Delete(executable);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        _server.Requests.Count.ShouldBe(2);
        File.Exists(executable).ShouldBeTrue();
        Leftovers().ShouldBeEmpty();
    }

    // -- releases as data ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Installs_a_release_that_is_not_the_pin_into_its_own_folder()
    {
        var release = Publish("1.4.3");
        var asset = release.AssetFor("linux-x64")!;
        var installer = NewInstaller(release);

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        result.Value.ShouldBe(new BunLocation(Path.Combine(Root, "1.4.3", "bun"), BunSources.Installed, "1.4.3"));
        _server.Requests.ShouldBe([$"/bun-v1.4.3/{asset.FileName}"]);
        installer.Job!.Version.ShouldBe("1.4.3");
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeFalse();
    }

    [Fact]
    public async Task Writes_the_rid_into_the_manifest()
    {
        var release = Publish();
        await NewInstaller(release, "linux-arm64").EnsureAsync(release, null, CancellationToken.None);

        var manifest = await File.ReadAllTextAsync(Path.Combine(Root, "1.4.2", "install.json"));

        manifest.ShouldContain("\"rid\": \"linux-arm64\"");
    }

    [Fact]
    public async Task The_pinned_release_still_works_as_the_default()
    {
        var pinned = BunRelease.Pinned;
        var asset = pinned.AssetFor("linux-x64")!;
        var bytes = Zip("linux-x64", pinned.Version);
        _server.Serve($"/bun-v{pinned.Version}/{asset.FileName}", bytes);
        var release = pinned with { Assets = [.. pinned.Assets.Select(a => a.Rid == "linux-x64" ? a with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } : a)] };
        var installer = NewInstaller(release);

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Value.Version.ShouldBe(pinned.Version);
        Directory.Exists(Path.Combine(Root, pinned.Version)).ShouldBeTrue();
    }

    // -- several installed versions ---------------------------------------------------------------------------

    [Fact]
    public async Task Keeps_returning_the_installed_version_while_a_newer_release_downloads()
    {
        var release = Publish("1.4.3", mode: BunServeMode.Hold);
        var old = Plant("1.4.2");
        var installer = NewInstaller(release);

        var install = installer.EnsureAsync(release, null, CancellationToken.None);
        await _server.Held.WaitAsync(TimeSpan.FromSeconds(10));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBe(new BunLocation(old, BunSources.Installed, "1.4.2"));

        _server.ReleaseHeld();
        var result = await install.WaitAsync(TimeSpan.FromSeconds(20));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        (await installer.FindAsync(release, CancellationToken.None))!.Version.ShouldBe("1.4.3");
    }

    [Fact]
    public async Task The_releases_version_wins_over_a_newer_installed_one()
    {
        var release = Publish();
        Plant("1.4.9");
        Plant("1.4.2", sha: release.AssetFor("linux-x64")!.Sha256);
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None))!.Version.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task Returns_the_newest_installed_version_when_the_releases_is_not_installed()
    {
        var release = Publish("1.4.5");
        Plant("1.4.2");
        Plant("1.4.4");
        Plant("1.4.3");
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None))!.Version.ShouldBe("1.4.4");
    }

    [Fact]
    public async Task Never_returns_a_version_below_the_minimum()
    {
        var release = Publish();
        Plant("1.3.9");
        Plant("1.0.0");
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_folder_for_the_releases_version_with_another_sha_does_not_count_and_is_replaced()
    {
        var release = Publish();
        Plant("1.4.2", sha: new string('a', 64));
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : "");
        _server.Requests.Count.ShouldBe(1);
        (await File.ReadAllTextAsync(Path.Combine(Root, "1.4.2", "install.json"))).ShouldContain(release.AssetFor("linux-x64")!.Sha256);
    }

    [Fact]
    public async Task A_manifest_for_another_platform_does_not_count()
    {
        var release = Publish("1.4.5");
        Plant("1.4.4", manifestRid: "osx-arm64");
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        installer.Installed().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_manifest_whose_version_differs_from_its_folder_does_not_count()
    {
        var release = Publish("1.4.5");
        Plant("1.4.4", manifestVersion: "1.4.3");
        var installer = NewInstaller(release);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        installer.Installed().ShouldBeEmpty();
    }

    [Fact]
    public void An_older_manifest_without_a_rid_still_counts_and_a_folder_without_the_executable_does_not()
    {
        Plant("1.4.2", manifestRid: null);
        Plant("1.4.3", executable: false);
        var installer = NewInstaller(Publish());

        installer.Installed().Select(l => l.Version).ShouldBe(["1.4.2"]);
    }

    [Fact]
    public void Installed_lists_every_version_newest_first_without_checking_a_sha()
    {
        var one = Plant("1.4.2");
        var two = Plant("1.4.10");
        var three = Plant("1.4.3");
        Directory.CreateDirectory(Path.Combine(Root, ".download-x"));
        Directory.CreateDirectory(Path.Combine(Root, "not-a-version"));
        var installer = NewInstaller(Publish());

        installer.Installed().ShouldBe(
        [
            new BunLocation(two, BunSources.Installed, "1.4.10"),
            new BunLocation(three, BunSources.Installed, "1.4.3"),
            new BunLocation(one, BunSources.Installed, "1.4.2"),
        ]);
    }

    // -- the configured Bun ---------------------------------------------------------------------------------------

    private async Task<string> ConfiguredFileAsync(string name = "my-bun.exe")
    {
        var path = Path.Combine(_home, name);
        await File.WriteAllTextAsync(path, Script("1.4.5"));
        return path;
    }

    [Fact]
    public async Task A_configured_bun_that_does_not_run_is_an_error_with_the_probes_reason()
    {
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var installer = NewInstaller(release, bunPath: configured, probe: Fails("it printed nothing."));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe($"Fleet:Harness:BunPath is {configured}, which didn't run: it printed nothing.");
        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_bun_older_than_the_minimum_is_an_error_and_nothing_downloads()
    {
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var installer = NewInstaller(release, bunPath: configured, probe: Prints("1.3.0"));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Mods.Runtime");
        result.Error.Description.ShouldBe(
            $"Bun 1.3.0 at {configured} is older than 1.4.0, the oldest Bun mods run on. Run bun upgrade, or use Fleet's own Bun.");
        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_bun_is_used_even_when_it_is_unsafe()
    {
        var release = Publish() with { OldestSafe = "1.4.2" };
        var configured = await ConfiguredFileAsync();
        var installer = NewInstaller(release, bunPath: configured, probe: Prints("1.4.1"));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBe(new BunLocation(configured, BunSources.Configured, "1.4.1"));
    }

    [Fact]
    public async Task A_configured_bun_runs_through_the_real_probe_and_bun_upgrade_is_noticed()
    {
        if (OperatingSystem.IsWindows())
            return;

        var folder = Path.Combine(_home, ".bun", "bin");
        Directory.CreateDirectory(folder);
        var bun = Path.Combine(folder, "bun");
        WriteScript(bun, "1.4.5");
        var installer = new BunRuntimeInstaller(
            new FleetOptions { Harness = { BunPath = bun } },
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = _home,
            DownloadBase = _server.BaseUri,
        };

        (await installer.FindAsync(BunRelease.Pinned, CancellationToken.None))
            .ShouldBe(new BunLocation(bun, BunSources.Configured, "1.4.5"));

        // bun upgrade writes a new binary in place.
        WriteScript(bun, "1.4.6");
        File.SetLastWriteTimeUtc(bun, DateTime.UtcNow.AddMinutes(1));

        (await installer.FindAsync(BunRelease.Pinned, CancellationToken.None))!.Version.ShouldBe("1.4.6");
        _server.Requests.ShouldBeEmpty();
    }

    private static void WriteScript(string path, string version)
    {
        File.WriteAllText(path, $"#!/bin/sh\necho {version}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Probes_a_configured_bun_once_for_repeated_finds()
    {
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var probed = 0;
        var installer = NewInstaller(release, bunPath: configured, probe: (_, _) =>
        {
            Interlocked.Increment(ref probed);
            return Task.FromResult(new BunProbeResult(BunVersion.Parse("1.4.5"), null));
        });

        await installer.FindAsync(release, CancellationToken.None);
        await installer.FindAsync(release, CancellationToken.None);
        await installer.EnsureAsync(release, null, CancellationToken.None);

        probed.ShouldBe(1);
    }

    [Fact]
    public async Task Probes_again_when_the_configured_file_is_replaced()
    {
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var probed = 0;
        var installer = NewInstaller(release, bunPath: configured, probe: (_, _) =>
        {
            Interlocked.Increment(ref probed);
            return Task.FromResult(new BunProbeResult(BunVersion.Parse("1.4.5"), null));
        });
        await installer.FindAsync(release, CancellationToken.None);

        await File.WriteAllTextAsync(configured, Script("1.4.6") + "# upgraded\n");
        File.SetLastWriteTimeUtc(configured, DateTime.UtcNow.AddMinutes(5));
        await installer.FindAsync(release, CancellationToken.None);

        probed.ShouldBe(2);
    }

    [Fact]
    public async Task Does_not_cache_a_failed_probe()
    {
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var probed = 0;
        var installer = NewInstaller(release, bunPath: configured, probe: (_, _) =>
            Task.FromResult(++probed == 1 ? new BunProbeResult(null, "timed out.") : new BunProbeResult(BunVersion.Parse("1.4.5"), null)));

        (await installer.FindAsync(release, CancellationToken.None)).ShouldBeNull();
        (await installer.FindAsync(release, CancellationToken.None))!.Version.ShouldBe("1.4.5");
    }

    // -- pruning ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prune_keeps_the_newest_and_the_one_in_use_and_returns_the_rest_oldest_first()
    {
        Plant("1.4.3");
        var inUse = Plant("1.4.1");
        Plant("1.4.0");
        Plant("1.4.4");
        Plant("1.4.2");
        var installer = NewInstaller(Publish("1.4.4"));

        var deleted = await installer.PruneAsync([inUse], CancellationToken.None);

        deleted.ShouldBe(["1.4.0", "1.4.2", "1.4.3"]);
        Directory.GetDirectories(Root).Select(Path.GetFileName).Order().ShouldBe(["1.4.1", "1.4.4"]);
    }

    [Fact]
    public async Task Prune_compares_folders_not_name_prefixes()
    {
        Plant("1.4.1");
        var newest = Plant("1.4.10");
        var installer = NewInstaller(Publish("1.4.10"));

        var deleted = await installer.PruneAsync([newest], CancellationToken.None);

        deleted.ShouldBe(["1.4.1"]);
    }

    [Fact]
    public async Task Prune_leaves_other_names_and_newer_broken_folders_alone()
    {
        Plant("1.4.3");
        Plant("1.4.1");
        var download = Path.Combine(Root, ".download-abc");
        var staging = Path.Combine(Root, ".staging-abc");
        var other = Path.Combine(Root, "notes");
        var newerBroken = Path.Combine(Root, "1.5.0");
        var olderBroken = Path.Combine(Root, "1.2.0");
        foreach (var directory in new[] { download, staging, other, newerBroken, olderBroken })
            Directory.CreateDirectory(directory);
        var installer = NewInstaller(Publish("1.4.3"));

        var deleted = await installer.PruneAsync([], CancellationToken.None);

        deleted.ShouldBe(["1.2.0", "1.4.1"]);
        foreach (var directory in new[] { download, staging, other, newerBroken })
            Directory.Exists(directory).ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_skips_a_folder_that_will_not_rename_and_still_deletes_the_others()
    {
        Plant("1.4.0");
        Plant("1.4.1");
        Plant("1.4.2");
        Plant("1.4.3");
        var installer = NewInstaller(Publish("1.4.3"), rename: (from, to) =>
        {
            if (Path.GetFileName(from) == "1.4.1")
                throw new IOException("The process cannot access the file because it is being used by another process.");
            Directory.Move(from, to);
        });

        var deleted = await installer.PruneAsync([], CancellationToken.None);

        deleted.ShouldBe(["1.4.0", "1.4.2"]);
        Directory.Exists(Path.Combine(Root, "1.4.1")).ShouldBeTrue();
        Leftovers().ShouldBeEmpty();
    }

    [Fact]
    public async Task Prune_with_nothing_to_prune_returns_empty()
    {
        var installer = NewInstaller(Publish());
        (await installer.PruneAsync([], CancellationToken.None)).ShouldBeEmpty();

        Plant("1.4.2");
        (await installer.PruneAsync([], CancellationToken.None)).ShouldBeEmpty();
        Directory.Exists(Path.Combine(Root, "1.4.2")).ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_waits_for_an_install_in_progress()
    {
        var release = Publish("1.4.3", mode: BunServeMode.Hold);
        Plant("1.4.1");
        Plant("1.4.2");
        var installer = NewInstaller(release);

        var install = installer.EnsureAsync(release, null, CancellationToken.None);
        await _server.Held.WaitAsync(TimeSpan.FromSeconds(10));
        var prune = installer.PruneAsync([], CancellationToken.None);

        await Task.Delay(300);
        prune.IsCompleted.ShouldBeFalse();

        _server.ReleaseHeld();
        (await install.WaitAsync(TimeSpan.FromSeconds(20))).IsSuccess.ShouldBeTrue();
        (await prune.WaitAsync(TimeSpan.FromSeconds(20))).ShouldBe(["1.4.1", "1.4.2"]);
    }

    // -- safety -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BunSources.Installed, "1.4.2", "1.4.2", "1.4.2", true, false)]
    [InlineData(BunSources.Installed, "1.4.2", "1.4.2", "1.4.3", true, true)]
    [InlineData(BunSources.Installed, "1.4.3", "1.4.2", "1.4.3", true, false)]
    [InlineData(BunSources.Installed, "1.5.0", "1.4.2", "1.4.3", true, false)]
    [InlineData(BunSources.Configured, "1.4.2", "1.4.2", "1.4.3", true, false)]
    [InlineData(BunSources.Installed, "1.4.1", "1.4.2", "1.4.3", false, true)]
    [InlineData(BunSources.Configured, "1.4.1", "1.4.2", "1.4.3", false, false)]
    [InlineData(BunSources.Configured, "1.4.3-canary.20", "1.4.3", "1.4.3", false, false)]
    [InlineData(BunSources.Installed, "1.4.3-canary.20", "1.4.3", "1.4.3", false, true)]
    public void Judges_safety_and_whether_an_update_is_coming(
        string source, string version, string oldestSafe, string wanted, bool safe, bool update)
    {
        var release = Publish(wanted) with { OldestSafe = oldestSafe };
        var installer = NewInstaller(release);

        var safety = installer.SafetyOf(new BunLocation("/x/bun", source, version), release);

        safety.Safe.ShouldBe(safe);
        safety.UpdateAvailable.ShouldBe(update);
        (safety.Message is null).ShouldBe(safe);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("It fixes a crash.", "It fixes a crash. ")]
    public void Words_the_warning_for_the_users_own_bun(string? note, string shown)
    {
        var release = Publish("1.4.3") with { OldestSafe = "1.4.2", Note = note };

        var safety = NewInstaller(release).SafetyOf(new BunLocation("/x/bun", BunSources.Configured, "1.4.1"), release);

        safety.Message.ShouldBe(
            $"Your Bun 1.4.1 needs a security fix: the oldest safe version is 1.4.2. {shown}" +
            "Run bun upgrade and Fleet picks up the new version by itself, or use Fleet's own Bun instead.");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("It fixes a crash.", "It fixes a crash. ")]
    public void Words_the_warning_for_fleets_own_bun(string? note, string shown)
    {
        var release = Publish("1.4.3") with { OldestSafe = "1.4.2", Note = note };

        var safety = NewInstaller(release).SafetyOf(new BunLocation("/x/bun", BunSources.Installed, "1.4.1"), release);

        safety.Message.ShouldBe(
            $"Fleet's Bun 1.4.1 needs a security fix: the oldest safe version is 1.4.2. {shown}" +
            "Fleet installs Bun 1.4.3 to replace it.");
    }

    // -- review round 1 -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Prune_keeps_the_one_in_use_when_home_is_reached_through_a_link_and_inUse_uses_the_real_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_home, "real-home");
        var linked = Path.Combine(_home, "linked-home");
        Directory.CreateSymbolicLink(linked, real);
        var root = Path.Combine(real, ".weave", "runtimes", "bun");
        Directory.CreateDirectory(Path.Combine(root, "1.4.1"));
        Directory.CreateDirectory(Path.Combine(root, "1.4.4"));
        foreach (var version in new[] { "1.4.1", "1.4.4" })
            PlantIn(root, version);
        var installer = NewInstaller(Publish("1.4.4"), home: linked);

        var deleted = await installer.PruneAsync([Path.Combine(real, ".weave", "runtimes", "bun", "1.4.1", "bun")], CancellationToken.None);

        deleted.ShouldBeEmpty();
        Directory.Exists(Path.Combine(root, "1.4.1")).ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_keeps_the_one_in_use_when_home_is_real_and_inUse_goes_through_a_link()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_home, "real-home");
        var linked = Path.Combine(_home, "linked-home");
        Directory.CreateSymbolicLink(linked, real);
        var root = Path.Combine(real, ".weave", "runtimes", "bun");
        foreach (var version in new[] { "1.4.1", "1.4.4" })
            PlantIn(root, version);
        var installer = NewInstaller(Publish("1.4.4"), home: real);

        var deleted = await installer.PruneAsync([Path.Combine(linked, ".weave", "runtimes", "bun", "1.4.1", "bun")], CancellationToken.None);

        deleted.ShouldBeEmpty();
        Directory.Exists(Path.Combine(root, "1.4.1")).ShouldBeTrue();
    }

    private static void PlantIn(string root, string version)
    {
        var folder = Path.Combine(root, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "bun"), "x");
        File.WriteAllText(
            Path.Combine(folder, "install.json"),
            $"{{ \"version\": \"{version}\", \"rid\": \"linux-x64\", \"assetFileName\": \"bun.zip\", " +
            $"\"sha256\": \"{new string('0', 64)}\", \"installedAt\": \"2026-10-01T00:00:00+00:00\" }}");
    }

    [Fact]
    [Trait("Category", "ModsFileSafety")]
    public async Task A_replacement_with_the_same_size_and_time_is_probed_again()
    {
        if (OperatingSystem.IsWindows()) return;
        var release = Publish();
        var configured = await ConfiguredFileAsync();
        var stamp = File.GetLastWriteTimeUtc(configured);
        var probed = 0;
        var installer = NewInstaller(release, bunPath: configured, probe: (_, _) =>
        {
            probed++;
            return Task.FromResult(new BunProbeResult(BunVersion.Parse("1.4.5"), null));
        });

        (await installer.FindAsync(release, CancellationToken.None)).ShouldNotBeNull();
        (await installer.FindAsync(release, CancellationToken.None)).ShouldNotBeNull();
        probed.ShouldBe(1);

        var replacement = Path.Combine(_home, "replacement");
        await File.WriteAllTextAsync(replacement, Script("1.4.5"));
        File.SetLastWriteTimeUtc(replacement, stamp);
        File.Move(replacement, configured, overwrite: true);

        (await installer.FindAsync(release, CancellationToken.None)).ShouldNotBeNull();
        probed.ShouldBe(2);
    }

    [Fact]
    public async Task A_configured_path_that_is_a_folder_says_so()
    {
        var release = Publish();
        var folder = Path.Combine(_home, "a-folder.exe");
        Directory.CreateDirectory(folder);
        var installer = NewInstaller(release, bunPath: folder);

        var result = await installer.EnsureAsync(release, null, CancellationToken.None);

        result.Error.Description.ShouldBe($"Fleet:Harness:BunPath is {folder}, which is a folder, not the bun program.");
        _server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("bun")]
    [InlineData("bun.cmd")]
    public async Task On_Windows_a_configured_bun_must_end_in_exe(string fileName)
    {
        // Fully qualified on this host, so the .exe rule is the one that fires, not the absolute-path rule.
        var configured = Path.Combine(Path.GetTempPath(), "tools", fileName);
        // Windows rules, tested on any machine through the seam.
        var installer = new BunRuntimeInstaller(
            new FleetOptions { Harness = { BunPath = configured } },
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = _home,
            IsWindows = true,
            Probe = Fails("Not expected to run."),
        };

        var result = await installer.EnsureAsync(BunRelease.Pinned, null, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Description.ShouldBe($"Fleet:Harness:BunPath is {configured}, which must end in .exe: point it at bun.exe.");
    }

    // -- helpers ------------------------------------------------------------------------------------------------

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Func<string, CancellationToken, Task<BunProbeResult>> Prints(string version) =>
        (_, _) => Task.FromResult(new BunProbeResult(BunVersion.Parse(version), null));

    private static Func<string, CancellationToken, Task<BunProbeResult>> Fails(string error) =>
        (_, _) => Task.FromResult(new BunProbeResult(null, error));

    private BunRuntimeInstaller NewInstaller(
        BunRelease release,
        string rid = "linux-x64",
        string? bunPath = null,
        Func<string, CancellationToken, Task<BunProbeResult>>? probe = null,
        TimeSpan? stall = null,
        Action<string, string>? move = null,
        Action<string, string>? rename = null,
        string? home = null,
        Uri? downloadBase = null) =>
        new(
            new FleetOptions { Harness = { BunPath = bunPath ?? "" } },
            new FakeHttpClientFactory(),
            NullLogger<BunRuntimeInstaller>.Instance)
        {
            Home = home ?? _home,
            Rid = rid,
            DownloadBase = downloadBase ?? _server.BaseUri,
            Probe = probe ?? Fails("Not expected to run."),
            StallTimeout = stall ?? TimeSpan.FromSeconds(60),
            MoveRetryDelay = TimeSpan.FromMilliseconds(1),
            MoveDirectory = move ?? Directory.Move,
            RenameDirectory = rename ?? Directory.Move,
        };

    /// <summary>Serves a fake archive for every pinned platform and returns a release whose checksums match them.</summary>
    private BunRelease Publish(
        string version = "1.4.2",
        int status = 200,
        BunServeMode mode = BunServeMode.Normal,
        Func<BunAsset, byte[]>? build = null)
    {
        var assets = new List<BunAsset>();
        foreach (var pinned in BunRelease.Pinned.Assets)
        {
            var bytes = build is null ? Zip(pinned.Rid, version) : build(pinned);
            _server.Serve($"/bun-v{version}/{pinned.FileName}", bytes, status, mode);
            assets.Add(pinned with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }

        return new BunRelease(version, assets);
    }

    /// <summary>Writes a version folder as an install would have left it, with the fields a test wants wrong changed.</summary>
    private string Plant(
        string version,
        string? sha = null,
        string? manifestVersion = null,
        string? manifestRid = "linux-x64",
        bool executable = true,
        string rid = "linux-x64")
    {
        var folder = Path.Combine(Root, version);
        Directory.CreateDirectory(folder);
        var ridField = manifestRid is null ? "" : $"\"rid\": \"{manifestRid}\", ";
        File.WriteAllText(
            Path.Combine(folder, "install.json"),
            $"{{ \"version\": \"{manifestVersion ?? version}\", {ridField}\"assetFileName\": \"bun.zip\", " +
            $"\"sha256\": \"{sha ?? new string('0', 64)}\", \"installedAt\": \"2026-10-01T00:00:00+00:00\" }}");
        var exe = Path.Combine(folder, rid.StartsWith("win-", StringComparison.Ordinal) ? "bun.exe" : "bun");
        if (executable)
            File.WriteAllText(exe, Script(version));
        return exe;
    }

    private static byte[] Zip(string rid, string version = "1.4.2") => MakeZip(BunRelease.Pinned.AssetFor(rid)!, version);

    private static byte[] MakeZip(BunAsset asset, string version = "1.4.2", bool includeExecutable = true, string? extraEntry = null)
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
                writer.Write(Script(version));
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
}
