using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class WriteToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public WriteToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_WriteToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new WriteTool();
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
        _fn.Name.ShouldBe("write");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WritesNewFile()
    {
        var filePath = Path.Combine(_tempDir, "new.txt");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["content"] = "hello world",
        });

        text.ShouldContain("new file created");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("hello world");
    }

    [Fact]
    public async Task OverwritesExistingFile()
    {
        var filePath = Path.Combine(_tempDir, "existing.txt");
        await File.WriteAllTextAsync(filePath, "old content");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["content"] = "new content",
        });

        text.ShouldContain("Wrote file successfully");
        text.ShouldNotContain("new file created");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("new content");
    }

    [Fact]
    public async Task CreatesParentDirectories()
    {
        var filePath = Path.Combine(_tempDir, "a", "b", "c", "deep.txt");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["content"] = "deep content",
        });

        text.ShouldContain("new file created");
        File.Exists(filePath).ShouldBeTrue();
        (await File.ReadAllTextAsync(filePath)).ShouldBe("deep content");
    }

    [Fact]
    public async Task WritesEmptyContent()
    {
        var filePath = Path.Combine(_tempDir, "empty.txt");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["content"] = "",
        });

        text.ShouldContain("Wrote file successfully");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyPath()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = "",
            ["content"] = "content",
        });

        text.ShouldContain("Error");
        text.ShouldContain("filePath is required");
    }
}
