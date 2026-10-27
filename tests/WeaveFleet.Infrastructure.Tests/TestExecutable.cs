using System.Diagnostics;
using System.Text;

namespace WeaveFleet.Infrastructure.Tests;

/// <summary>
/// Writes a file a test then runs. On Linux and macOS a child process writes it: a file this process writes can't
/// always be run straight away while tests run in parallel. A process another test starts at that moment holds a
/// copy of the open file until it execs, and running the file then fails with "Text file busy".
/// </summary>
public static class TestExecutable
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/> with mode 700 (read, write and run, for the user only).</summary>
    public static void Write(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false), UseShellExecute = false };
        foreach (var argument in (string[])["-c", "cat > \"$1\" && chmod 700 \"$1\"", "sh", path])
            start.ArgumentList.Add(argument);

        using var writer = Process.Start(start)!;
        writer.StandardInput.Write(contents);
        writer.StandardInput.Close();
        writer.WaitForExit();
        writer.ExitCode.ShouldBe(0, $"Couldn't write {path}");
    }
}
