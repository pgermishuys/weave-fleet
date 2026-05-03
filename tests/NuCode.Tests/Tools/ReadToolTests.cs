using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class ReadToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public ReadToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_ReadToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new ReadTool();
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
        _fn.Name.ShouldBe("read");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ReadsFileWithLineNumbers()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "line one\nline two\nline three\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
        });

        text.ShouldContain("1: line one");
        text.ShouldContain("2: line two");
        text.ShouldContain("3: line three");
    }

    [Fact]
    public async Task ReadsFileWithOffset()
    {
        var filePath = Path.Combine(_tempDir, "offset.txt");
        await File.WriteAllTextAsync(filePath, "a\nb\nc\nd\ne\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["offset"] = 3,
        });

        text.ShouldContain("3: c");
        text.ShouldNotContain("1: a");
        text.ShouldNotContain("2: b");
    }

    [Fact]
    public async Task ReadsFileWithLimit()
    {
        var filePath = Path.Combine(_tempDir, "limit.txt");
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}");
        await File.WriteAllLinesAsync(filePath, lines);

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["limit"] = 5,
        });

        text.ShouldContain("1: line 1");
        text.ShouldContain("5: line 5");
        text.ShouldNotContain("6: line 6");
        text.ShouldContain("Showing lines");
    }

    [Fact]
    public async Task ReadsDirectory()
    {
        var subDir = Path.Combine(_tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "content");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = _tempDir,
        });

        text.ShouldContain("directory");
        text.ShouldContain("file.txt");
        text.ShouldContain("subdir/");
    }

    [Fact]
    public async Task ReturnsErrorForMissingPath()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = Path.Combine(_tempDir, "nonexistent.txt"),
        });

        text.ShouldContain("Error");
        text.ShouldContain("does not exist");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyPath()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = "",
        });

        text.ShouldContain("Error");
        text.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task ReturnsErrorForInvalidOffset()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "content");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["offset"] = 0,
        });

        text.ShouldContain("Error");
    }

    [Fact]
    public async Task DetectsBinaryFileByExtension()
    {
        var filePath = Path.Combine(_tempDir, "app.exe");
        await File.WriteAllBytesAsync(filePath, [0x4D, 0x5A, 0x90, 0x00]);

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
        });

        text.ShouldContain("[binary file:");
    }

    [Fact]
    public async Task DetectsImageFileByExtension()
    {
        var filePath = Path.Combine(_tempDir, "logo.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47]);

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
        });

        text.ShouldContain("[image file:");
        text.ShouldContain("logo.png");
    }

    [Fact]
    public async Task TruncatesLongLines()
    {
        var filePath = Path.Combine(_tempDir, "long.txt");
        var longLine = new string('x', 3000);
        await File.WriteAllTextAsync(filePath, longLine);

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
        });

        text.ShouldContain("[truncated]");
    }

    [Fact]
    public async Task ReturnsErrorWhenOffsetBeyondEndOfFile()
    {
        var filePath = Path.Combine(_tempDir, "short.txt");
        await File.WriteAllTextAsync(filePath, "one\ntwo\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["offset"] = 100,
        });

        text.ShouldContain("Error");
        text.ShouldContain("beyond end of file");
    }
}
