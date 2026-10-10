using System.Diagnostics;
using WeaveFleet.Infrastructure.Mods;

namespace WeaveFleet.Infrastructure.Tests.Mods;

/// <summary>
/// Opening a file and checking what it is, in one step. On Linux x64 these also prove the <c>struct stat</c> offsets
/// (mode at 24, size at 48) against a known file, a named pipe and a directory.
/// </summary>
public sealed class SafeFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"fleet-safefile-{Guid.NewGuid():N}");

    public SafeFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string At(string name) => Path.Combine(_folder, name);

    private static FileProblem Open(string path, long max, out long length)
    {
        var problem = SafeFile.Open(path, max, out var stream);
        using (stream)
        {
            length = stream?.Length ?? -1;
            return problem;
        }
    }

    [Fact]
    public void A_regular_file_opens_and_its_size_is_checked_against_the_cap_to_the_byte()
    {
        File.WriteAllBytes(At("f"), new byte[1234]);

        Open(At("f"), 1234, out var length).ShouldBe(FileProblem.None);
        length.ShouldBe(1234);
        Open(At("f"), 1233, out _).ShouldBe(FileProblem.TooLarge);
        Open(At("f"), 5000, out _).ShouldBe(FileProblem.None);
    }

    [Fact]
    public void An_empty_file_opens()
    {
        File.WriteAllBytes(At("e"), []);

        Open(At("e"), 10, out _).ShouldBe(FileProblem.None);
    }

    [Fact]
    public void A_missing_file_is_missing()
        => Open(At("nope"), 10, out _).ShouldBe(FileProblem.Missing);

    [Fact]
    public void A_directory_is_not_a_regular_file()
    {
        Directory.CreateDirectory(At("d"));

        Open(At("d"), 10, out _).ShouldBe(FileProblem.NotRegular);
    }

    [Fact]
    public void A_named_pipe_is_not_a_regular_file_and_opening_it_does_not_wait()
    {
        if (OperatingSystem.IsWindows())
            return;
        using (var process = Process.Start("mkfifo", At("p"))!)
            process.WaitForExit();

        var watch = Stopwatch.StartNew();
        Open(At("p"), 10, out _).ShouldBe(FileProblem.NotRegular);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_symbolic_link_is_refused_even_to_a_regular_file()
    {
        File.WriteAllText(At("real"), "x");
        File.CreateSymbolicLink(At("link"), At("real"));

        Open(At("link"), 10, out _).ShouldBe(FileProblem.Link);
    }

    [Fact]
    public void A_device_is_not_a_regular_file()
    {
        if (!File.Exists("/dev/null"))
            return;

        Open("/dev/null", 10, out _).ShouldBe(FileProblem.NotRegular);
    }

    [Fact]
    public void A_file_with_no_permissions_cannot_be_read()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;
        File.WriteAllText(At("locked"), "x");
        File.SetUnixFileMode(At("locked"), 0);

        Open(At("locked"), 10, out _).ShouldBe(FileProblem.Unreadable);
    }

    [Fact]
    public void A_sparse_file_of_5_GB_is_too_large_at_once()
    {
        using (var big = new FileStream(At("big"), FileMode.Create))
            big.SetLength(5L * 1024 * 1024 * 1024);

        var watch = Stopwatch.StartNew();
        Open(At("big"), 16 * 1024 * 1024, out _).ShouldBe(FileProblem.TooLarge);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ReadAll_returns_the_bytes_up_to_the_cap()
    {
        File.WriteAllText(At("t"), "hello");

        SafeFile.ReadAll(At("t"), 5, out var data).ShouldBe(FileProblem.None);
        data.ShouldBe("hello"u8.ToArray());
        SafeFile.ReadAll(At("t"), 4, out _).ShouldBe(FileProblem.TooLarge);
    }
}
