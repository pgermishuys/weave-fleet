using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class ShellCommandTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("/home/you/.local/bin/claude", "/home/you/.local/bin/claude")]
    [InlineData("/Users/Jo Smith/.local/bin/claude", "'/Users/Jo Smith/.local/bin/claude'")]
    [InlineData("/home/o'brien/bin/claude", "'/home/o'\\''brien/bin/claude'")]
    public void Quotes_a_POSIX_path_only_when_it_needs_it(string executable, string expected)
    {
        ShellCommand.Executable(executable, windows: false).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\jo\.local\bin\claude.exe", @"C:\Users\jo\.local\bin\claude.exe")]
    [InlineData(@"C:\Users\Jo Smith\.local\bin\claude.exe", @"& 'C:\Users\Jo Smith\.local\bin\claude.exe'")]
    [InlineData(@"C:\Users\o'brien\claude.exe", @"& 'C:\Users\o''brien\claude.exe'")]
    public void Quotes_a_PowerShell_path_with_the_call_operator(string executable, string expected)
    {
        ShellCommand.Executable(executable, windows: true).ShouldBe(expected);
    }
}
