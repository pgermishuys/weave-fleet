using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class BashToolTests
{
    private readonly AIFunction _fn;

    public BashToolTests()
    {
        var tool = new BashTool();
        _fn = tool.ToAIFunction();
    }

    private async Task<string> InvokeAsync(Dictionary<string, object?> args)
    {
        var result = await _fn.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    [Fact]
    public void NameAndDescriptionAreSet()
    {
        _fn.Name.ShouldBe("bash");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ExecutesSimpleEchoCommand()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hello",
            ["description"] = "Prints hello",
        });

        text.ShouldContain("hello");
    }

    [Fact]
    public async Task CapturesExitCode()
    {
        // Use a command that exits with non-zero code
        var command = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? "exit /b 42"
            : "exit 42";

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["description"] = "Exit with code 42",
        });

        text.ShouldContain("Exit code: 42");
    }

    [Fact]
    public async Task RespectsWorkingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NuCode_BashTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
                ? "cd"
                : "pwd";

            var text = await InvokeAsync(new Dictionary<string, object?>
            {
                ["command"] = command,
                ["description"] = "Print working directory",
                ["workdir"] = tempDir,
            });

            text.ShouldContain(tempDir, Case.Insensitive);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TimesOutLongRunningCommand()
    {
        var command = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? "ping -n 30 127.0.0.1"
            : "sleep 30";

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["description"] = "Long running command",
            ["timeout"] = 1000,
        });

        text.ShouldContain("bash_metadata");
        text.ToLowerInvariant().ShouldContain("timeout");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyCommand()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = "",
            ["description"] = "Empty command",
        });

        text.ShouldContain("Error");
        text.ShouldContain("command is required");
    }

    [Fact]
    public async Task ReturnsErrorForInvalidTimeout()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["description"] = "test",
            ["timeout"] = -1,
        });

        text.ShouldContain("Error");
        text.ShouldContain("positive number");
    }

    [Fact]
    public async Task ReturnsErrorForNonexistentWorkdir()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["description"] = "test",
            ["workdir"] = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N")),
        });

        text.ShouldContain("Error");
        text.ShouldContain("does not exist");
    }

    [Fact]
    public async Task CapturesStderr()
    {
        var command = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? "echo error_output 1>&2"
            : "echo error_output >&2";

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["description"] = "Outputs to stderr",
        });

        text.ShouldContain("error_output");
    }
}
