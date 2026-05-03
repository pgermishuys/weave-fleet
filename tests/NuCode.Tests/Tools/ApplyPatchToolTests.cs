using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class ApplyPatchToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public ApplyPatchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_ApplyPatchToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new ApplyPatchTool();
        _fn = tool.ToAIFunction();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private async Task<string> InvokeAsync(string patchText)
    {
        var result = await _fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["patchText"] = patchText,
        }));
        return result?.ToString() ?? "";
    }

    [Fact]
    public void NameAndDescriptionAreSet()
    {
        _fn.Name.ShouldBe("apply_patch");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddsNewFile()
    {
        var filePath = Path.Combine(_tempDir, "new-file.txt");
        var patch = $"*** Add File: {filePath}\n+line one\n+line two\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        File.Exists(filePath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(filePath);
        content.ShouldContain("line one");
        content.ShouldContain("line two");
    }

    [Fact]
    public async Task AddsNewFileCreatesParentDirectories()
    {
        var filePath = Path.Combine(_tempDir, "sub", "deep", "file.txt");
        var patch = $"*** Add File: {filePath}\n+hello\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        File.Exists(filePath).ShouldBeTrue();
    }

    [Fact]
    public async Task DeletesFile()
    {
        var filePath = Path.Combine(_tempDir, "to-delete.txt");
        await File.WriteAllTextAsync(filePath, "content");

        var patch = $"*** Delete File: {filePath}\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        result.ShouldContain("Deleted");
        File.Exists(filePath).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteNonExistentFileReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "ghost.txt");
        var patch = $"*** Delete File: {filePath}\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("error");
        result.ShouldContain("not found");
    }

    [Fact]
    public async Task UpdatesFileWithHunk()
    {
        var filePath = Path.Combine(_tempDir, "update.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        var patch = $"""
            *** Update File: {filePath}
            @@ -1,3 +1,3 @@
             line1
            -line2
            +replaced
             line3
            """;

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        var content = await File.ReadAllTextAsync(filePath);
        content.ShouldContain("replaced");
        content.ShouldNotContain("line2");
    }

    [Fact]
    public async Task UpdateNonExistentFileReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "missing.txt");
        var patch = $"*** Update File: {filePath}\n@@ -1,1 +1,1 @@\n-old\n+new\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("error");
        result.ShouldContain("not found");
    }

    [Fact]
    public async Task MovesFile()
    {
        var filePath = Path.Combine(_tempDir, "original.txt");
        var movePath = Path.Combine(_tempDir, "moved.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");

        var patch = $"""
            *** Update File: {filePath}
            *** Move to: {movePath}
            @@ -1,2 +1,2 @@
             line1
            -line2
            +modified
            """;

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        File.Exists(filePath).ShouldBeFalse();
        File.Exists(movePath).ShouldBeTrue();
        (await File.ReadAllTextAsync(movePath)).ShouldContain("modified");
    }

    [Fact]
    public async Task MultipleOperationsInOnePatch()
    {
        var file1 = Path.Combine(_tempDir, "multi1.txt");
        var file2 = Path.Combine(_tempDir, "multi2.txt");
        await File.WriteAllTextAsync(file1, "existing");

        var patch = $"*** Delete File: {file1}\n*** Add File: {file2}\n+new content\n";

        var result = await InvokeAsync(patch);

        result.ShouldContain("successfully");
        File.Exists(file1).ShouldBeFalse();
        File.Exists(file2).ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyPatchTextReturnsError()
    {
        var result = await InvokeAsync("");
        result.ShouldContain("Error");
    }

    [Fact]
    public async Task MalformedPatchWithNoMarkersReturnsError()
    {
        var result = await InvokeAsync("just some random text\nwith no markers\n");
        result.ShouldContain("No valid patch operations");
    }
}
