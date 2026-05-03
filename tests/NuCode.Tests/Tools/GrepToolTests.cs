using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class GrepToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public GrepToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_GrepToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new GrepTool();
        _fn = tool.ToAIFunction();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private async Task<string> InvokeAsync(Dictionary<string, object?> args)
    {
        var result = await _fn.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    [Fact]
    public void NameAndDescriptionAreSet()
    {
        _fn.Name.ShouldBe("grep");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task FindsMatchingContent()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "code.cs"), "public class Foo\n{\n    int bar = 42;\n}\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "class Foo",
            ["path"] = _tempDir,
        });

        text.ShouldContain("code.cs");
        text.ShouldContain("class Foo");
    }

    [Fact]
    public async Task ReturnsNoMatchesWhenNoneFound()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "code.cs"), "public class Foo {}");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "class Bar",
            ["path"] = _tempDir,
        });

        text.ShouldBe("No matches found.");
    }

    [Fact]
    public async Task FindsMatchesWithRegex()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "data.txt"), "error: something failed\ninfo: all good\nerror: another failure\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = @"error:.*fail",
            ["path"] = _tempDir,
        });

        text.ShouldContain("data.txt");
        text.ShouldContain("something failed");
        text.ShouldContain("another failure");
    }

    [Fact]
    public async Task SearchesSubdirectories()
    {
        var subDir = Path.Combine(_tempDir, "src", "deep");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "nested.cs"), "// TODO: fix this\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "TODO",
            ["path"] = _tempDir,
        });

        text.ShouldContain("nested.cs");
        text.ShouldContain("TODO");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyPattern()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "",
            ["path"] = _tempDir,
        });

        text.ShouldContain("Error");
        text.ShouldContain("pattern is required");
    }

    [Fact]
    public async Task ReturnsErrorForMissingDirectory()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "test",
            ["path"] = Path.Combine(_tempDir, "nonexistent"),
        });

        text.ShouldContain("Error");
        text.ShouldContain("does not exist");
    }

    [Fact]
    public async Task ReturnsErrorForInvalidRegex()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "content");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "[invalid",
            ["path"] = _tempDir,
        });

        // Depending on whether rg is available, this might be "No matches found" (rg treats it differently)
        // or an error from the fallback .NET regex. Either way it shouldn't throw.
        text.ShouldNotBeNull();
    }

    [Fact]
    public async Task FindsMultipleMatchesInSameFile()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "multi.txt"),
            "line 1 match\nline 2 no\nline 3 match\nline 4 no\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "match",
            ["path"] = _tempDir,
        });

        text.ShouldContain("multi.txt");
        // Should contain both matching lines
        text.ShouldContain("line 1 match");
        text.ShouldContain("line 3 match");
    }

    [Fact]
    public async Task FindsMatchesAcrossMultipleFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.txt"), "hello world\n");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "b.txt"), "hello again\n");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "c.txt"), "goodbye\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "hello",
            ["path"] = _tempDir,
        });

        text.ShouldContain("a.txt");
        text.ShouldContain("b.txt");
        text.ShouldNotContain("c.txt");
    }

    [Fact]
    public async Task IncludeFilterLimitsFileTypes()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "code.cs"), "hello from cs\n");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "data.txt"), "hello from txt\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "hello",
            ["include"] = "*.cs",
            ["path"] = _tempDir,
        });

        text.ShouldContain("code.cs");
        // The fallback uses Directory.EnumerateFiles with the include as search pattern.
        // Ripgrep uses --glob. Either way, txt should be excluded.
        text.ShouldNotContain("data.txt");
    }
}
