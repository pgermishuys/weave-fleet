using System.IO.Compression;
using System.Text;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class SafeZipExtractorTests : IDisposable
{
    private const uint Regular0755 = 0x81ED;
    private const uint Regular0644 = 0x81A4;
    private const uint Symlink0777 = 0xA1FF;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "safezip-" + Guid.NewGuid().ToString("N"));
    private readonly string _zip;
    private readonly string _dest;

    public SafeZipExtractorTests()
    {
        Directory.CreateDirectory(_root);
        _zip = Path.Combine(_root, "in.zip");
        _dest = Path.Combine(_root, "out", "dest");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void BuildZip(params (string Name, string Content, uint Mode)[] entries)
    {
        using var stream = File.Create(_zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content, mode) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (mode != 0) entry.ExternalAttributes = (int)(mode << 16);
            using var writer = entry.Open();
            writer.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private void ExtractShouldBeRefused(params (string Name, string Content, uint Mode)[] entries)
    {
        BuildZip(entries);
        Should.Throw<InvalidDataException>(() => SafeZipExtractor.Extract(_zip, _dest));
        File.Exists(Path.Combine(Path.GetDirectoryName(_dest)!, "evil")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "evil")).ShouldBeFalse();
    }

    [Fact]
    public void A_normal_archive_with_nested_folders_unpacks()
    {
        BuildZip(
            ("top/", "", 0),
            ("top/bun", "binary", Regular0755),
            ("top/lib/deep/readme.txt", "hello", Regular0644),
            ("top/empty/", "", 0));

        SafeZipExtractor.Extract(_zip, _dest);

        File.ReadAllText(Path.Combine(_dest, "top", "bun")).ShouldBe("binary");
        File.ReadAllText(Path.Combine(_dest, "top", "lib", "deep", "readme.txt")).ShouldBe("hello");
        Directory.Exists(Path.Combine(_dest, "top", "empty")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("..\\evil")]
    [InlineData("/tmp/evil")]
    [InlineData("\\evil")]
    [InlineData("C:/evil")]
    [InlineData("C:evil")]
    [InlineData(".")]
    public void An_entry_that_leaves_the_folder_is_refused(string name)
    {
        ExtractShouldBeRefused(("ok.txt", "fine", 0), (name, "bad", 0));
    }

    [Fact]
    public void A_name_with_a_nul_is_refused()
    {
        ExtractShouldBeRefused(("a\0b", "bad", 0));
    }

    [Fact]
    public void A_symlink_pointing_outside_is_refused()
    {
        ExtractShouldBeRefused(("link", "../../etc", Symlink0777));
    }

    [Fact]
    public void A_symlink_pointing_inside_is_refused()
    {
        ExtractShouldBeRefused(("link", "bun", Symlink0777));
    }

    [Theory]
    [InlineData(0x11A4u)] // FIFO
    [InlineData(0x21A4u)] // character device
    [InlineData(0x61A4u)] // block device
    public void A_special_file_type_is_refused(uint mode)
    {
        ExtractShouldBeRefused(("special", "", mode));
    }

    [Fact]
    public void Nothing_is_written_when_a_later_entry_is_refused()
    {
        BuildZip(("fine.txt", "ok", 0), ("../evil", "bad", 0));

        Should.Throw<InvalidDataException>(() => SafeZipExtractor.Extract(_zip, _dest));

        Directory.GetFileSystemEntries(_dest).ShouldBeEmpty();
    }

    [Fact]
    public void The_same_path_twice_is_refused_ignoring_case()
    {
        BuildZip(("bun", "a", 0), ("BUN", "b", 0));

        var ex = Should.Throw<InvalidDataException>(() => SafeZipExtractor.Extract(_zip, _dest));
        ex.Message.ShouldContain("BUN");
    }

    [Fact]
    public void An_archive_over_the_size_limit_is_refused()
    {
        BuildZip(("big.bin", new string('x', 1000), 0));

        Should.Throw<InvalidDataException>(() => SafeZipExtractor.Extract(_zip, _dest, maxBytes: 100));
    }

    [Fact]
    public void An_executable_entry_is_executable_after_unpacking()
    {
        if (OperatingSystem.IsWindows()) return;
        BuildZip(("bun", "x", Regular0755));

        SafeZipExtractor.Extract(_zip, _dest);

        var mode = File.GetUnixFileMode(Path.Combine(_dest, "bun"));
        mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        mode.HasFlag(UnixFileMode.OtherExecute).ShouldBeTrue();
    }

    [Fact]
    public void A_non_executable_entry_stays_non_executable()
    {
        if (OperatingSystem.IsWindows()) return;
        BuildZip(("notes.txt", "x", Regular0644));

        SafeZipExtractor.Extract(_zip, _dest);

        var mode = File.GetUnixFileMode(Path.Combine(_dest, "notes.txt"));
        (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)).ShouldBe((UnixFileMode)0);
    }

    [Fact]
    public void The_setuid_bit_is_dropped()
    {
        if (OperatingSystem.IsWindows()) return;
        BuildZip(("bun", "x", 0x89EDu));

        SafeZipExtractor.Extract(_zip, _dest);

        var mode = File.GetUnixFileMode(Path.Combine(_dest, "bun"));
        mode.HasFlag(UnixFileMode.SetUser).ShouldBeFalse();
        mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
    }

    [Fact]
    public void An_entry_without_a_mode_unpacks()
    {
        if (OperatingSystem.IsWindows()) return;
        BuildZip(("plain.txt", "x", 0));

        SafeZipExtractor.Extract(_zip, _dest);

        File.ReadAllText(Path.Combine(_dest, "plain.txt")).ShouldBe("x");
    }

    [Fact]
    public void A_destination_that_is_not_empty_is_refused()
    {
        BuildZip(("a.txt", "x", 0));
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "existing"), "keep");

        Should.Throw<IOException>(() => SafeZipExtractor.Extract(_zip, _dest));
    }

    [Fact]
    public void A_missing_destination_is_created()
    {
        BuildZip(("a.txt", "x", 0));
        Directory.Exists(_dest).ShouldBeFalse();

        SafeZipExtractor.Extract(_zip, _dest);

        File.Exists(Path.Combine(_dest, "a.txt")).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_existing_destination_is_accepted()
    {
        BuildZip(("a.txt", "x", 0));
        Directory.CreateDirectory(_dest);

        SafeZipExtractor.Extract(_zip, _dest);

        File.Exists(Path.Combine(_dest, "a.txt")).ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_token_stops_the_unpack()
    {
        BuildZip(("a.txt", "x", 0));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => SafeZipExtractor.Extract(_zip, _dest, ct: cts.Token));
    }
}
