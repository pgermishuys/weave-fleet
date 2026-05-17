using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class EditToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public EditToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_EditToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new EditTool();
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
        _fn.Name.ShouldBe("edit");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ReplacesSingleOccurrence()
    {
        var filePath = Path.Combine(_tempDir, "single.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "hello",
            ["newString"] = "goodbye",
        });

        text.ShouldContain("Edit applied successfully");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("goodbye world");
    }

    [Fact]
    public async Task ReplacesAllOccurrences()
    {
        var filePath = Path.Combine(_tempDir, "multi.txt");
        await File.WriteAllTextAsync(filePath, "foo bar foo baz foo");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "foo",
            ["newString"] = "qux",
            ["replaceAll"] = true,
        });

        text.ShouldContain("Edit applied successfully");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("qux bar qux baz qux");
    }

    [Fact]
    public async Task ReturnsErrorWhenOldStringNotFound()
    {
        var filePath = Path.Combine(_tempDir, "notfound.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "xyz",
            ["newString"] = "abc",
        });

        text.ShouldContain("oldString not found");
    }

    [Fact]
    public async Task ReturnsErrorForMultipleMatchesWithoutReplaceAll()
    {
        var filePath = Path.Combine(_tempDir, "dupes.txt");
        await File.WriteAllTextAsync(filePath, "foo bar foo");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "foo",
            ["newString"] = "baz",
        });

        text.ShouldContain("multiple matches");
        // File should be unchanged
        (await File.ReadAllTextAsync(filePath)).ShouldBe("foo bar foo");
    }

    [Fact]
    public async Task ReturnsErrorForIdenticalStrings()
    {
        var filePath = Path.Combine(_tempDir, "same.txt");
        await File.WriteAllTextAsync(filePath, "content");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "content",
            ["newString"] = "content",
        });

        text.ShouldContain("identical");
    }

    [Fact]
    public async Task ReturnsErrorForMissingFile()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = Path.Combine(_tempDir, "missing.txt"),
            ["oldString"] = "foo",
            ["newString"] = "bar",
        });

        text.ShouldContain("File not found");
    }

    [Fact]
    public async Task ReturnsErrorForDirectory()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = _tempDir,
            ["oldString"] = "foo",
            ["newString"] = "bar",
        });

        text.ToLowerInvariant().ShouldContain("directory");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyPath()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = "",
            ["oldString"] = "foo",
            ["newString"] = "bar",
        });

        text.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task PreservesLineEndings()
    {
        var filePath = Path.Combine(_tempDir, "crlf.txt");
        await File.WriteAllTextAsync(filePath, "line1\r\nline2\r\nline3\r\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "line2",
            ["newString"] = "replaced",
        });

        text.ShouldContain("Edit applied successfully");
        var result = await File.ReadAllTextAsync(filePath);
        result.ShouldContain("\r\n");
        result.ShouldContain("replaced");
    }

    [Fact]
    public async Task HandlesMultiLineReplacement()
    {
        var filePath = Path.Combine(_tempDir, "multiline.txt");
        await File.WriteAllTextAsync(filePath, "start\nfoo\nbar\nend\n");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["oldString"] = "foo\nbar",
            ["newString"] = "replaced",
        });

        text.ShouldContain("Edit applied successfully");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("start\nreplaced\nend\n");
    }
}
