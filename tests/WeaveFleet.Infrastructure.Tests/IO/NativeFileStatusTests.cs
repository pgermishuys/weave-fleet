using System.Diagnostics;
using System.Globalization;
using WeaveFleet.Infrastructure.IO;

namespace WeaveFleet.Infrastructure.Tests.IO;

/// <summary>
/// The shared file-status call (libSystem.Native's <c>FileStatus</c>) against what the OS's own <c>stat</c> and <c>id</c>
/// report, on every Unix Fleet ships for (CI's "Mod file safety" matrix runs these on macOS and Linux arm64).
/// </summary>
[Trait("Category", "ModsFileSafety")]
public sealed class NativeFileStatusTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"fleet-filestatus-{Guid.NewGuid():N}");

    public NativeFileStatusTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string At(string name) => Path.Combine(_folder, name);

    /// <summary>The OS tool's uid, gid, inode, device and permission bits, with links followed.</summary>
    private static string[] OsStat(string path)
    {
        var line = OperatingSystem.IsMacOS()
            ? Run("stat", "-L", "-f", "%u %g %i %d %Lp", path)
            : Run("stat", "-L", "-c", "%u %g %i %d %a", path);
        return line.Split(' ');
    }

    private static string Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return text;
    }

    [Fact]
    public void FileStatus_layout_changed_if_this_fails_owner_ids_device_inode_and_mode_of_a_known_file()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.WriteAllBytes(At("known"), new byte[1234]);
        File.SetUnixFileMode(At("known"), (UnixFileMode)Convert.ToInt32("640", 8));
        var os = OsStat(At("known"));

        NativeFileStatus.TryStat(At("known"), out var status).ShouldBeTrue("FileStatus layout changed: stat failed");

        status.Uid.ToString(CultureInfo.InvariantCulture).ShouldBe(os[0], "FileStatus layout changed: Uid is not at the expected place");
        status.Gid.ToString(CultureInfo.InvariantCulture).ShouldBe(os[1], "FileStatus layout changed: Gid is not at the expected place");
        status.Ino.ToString(CultureInfo.InvariantCulture).ShouldBe(os[2], "FileStatus layout changed: Ino is not at the expected place");
        // stat's %d is st_dev as one number, and FileStatus.Dev is the same st_dev unchanged (no major/minor split), so they compare as they are.
        status.Dev.ToString(CultureInfo.InvariantCulture).ShouldBe(os[3], "FileStatus layout changed: Dev is not at the expected place");
        (status.Mode & 0xFFF).ShouldBe(Convert.ToInt32(os[4], 8), "FileStatus layout changed: Mode permission bits are not at the expected place");
        (status.Mode & 0xFFF).ShouldBe(Convert.ToInt32("640", 8));
        (status.Mode & 0xF000).ShouldBe(0x8000, "FileStatus layout changed: Mode type bits are not at the expected place");
        status.Size.ShouldBe(1234, "FileStatus layout changed: Size is not at the expected place");
    }

    [Fact]
    public void The_effective_ids_match_id()
    {
        if (OperatingSystem.IsWindows())
            return;

        NativeFileStatus.EffectiveUserId().ShouldBe(uint.Parse(Run("id", "-u"), CultureInfo.InvariantCulture));
        NativeFileStatus.EffectiveGroupId().ShouldBe(uint.Parse(Run("id", "-g"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_folder_is_a_folder_and_the_root_is_owned_by_root()
    {
        if (OperatingSystem.IsWindows())
            return;

        NativeFileStatus.TryStat(_folder, out var folder).ShouldBeTrue();
        (folder.Mode & 0xF000).ShouldBe(0x4000);
        NativeFileStatus.TryStat("/", out var root).ShouldBeTrue();
        root.Uid.ShouldBe(0u);
    }

    [Fact]
    public void Stat_follows_a_link_and_lstat_reports_the_link()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.WriteAllText(At("real"), "x");
        File.CreateSymbolicLink(At("link"), At("real"));

        NativeFileStatus.TryStat(At("link"), out var followed).ShouldBeTrue();
        NativeFileStatus.TryLStat(At("link"), out var itself).ShouldBeTrue();
        NativeFileStatus.TryStat(At("real"), out var real).ShouldBeTrue();

        (followed.Mode & 0xF000).ShouldBe(0x8000);
        (itself.Mode & 0xF000).ShouldBe(0xA000);
        followed.Ino.ShouldBe(real.Ino);
        itself.Ino.ShouldNotBe(real.Ino);
    }

    [Fact]
    public void A_missing_file_fails()
    {
        if (OperatingSystem.IsWindows())
            return;

        NativeFileStatus.TryStat(At("nothing"), out _).ShouldBeFalse();
        NativeFileStatus.TryLStat(At("nothing"), out _).ShouldBeFalse();
    }

    [Fact]
    public void Everything_fails_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        NativeFileStatus.TryStat(_folder, out _).ShouldBeFalse();
        NativeFileStatus.EffectiveUserId().ShouldBeNull();
        NativeFileStatus.EffectiveGroupId().ShouldBeNull();
    }
}
