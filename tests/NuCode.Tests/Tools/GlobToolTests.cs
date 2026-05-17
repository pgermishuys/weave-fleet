using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class GlobToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AIFunction _fn;

    public GlobToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_GlobToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var tool = new GlobTool();
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
        _fn.Name.ShouldBe("glob");
        _fn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task MatchesCsFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file1.cs"), "// code");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file2.cs"), "// code");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file3.txt"), "text");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
            ["path"] = _tempDir,
        });

        text.ShouldContain("file1.cs");
        text.ShouldContain("file2.cs");
        text.ShouldNotContain("file3.txt");
    }

    [Fact]
    public async Task MatchesFilesInSubdirectories()
    {
        var subDir = Path.Combine(_tempDir, "src", "lib");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "deep.cs"), "// deep");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "root.cs"), "// root");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
            ["path"] = _tempDir,
        });

        text.ShouldContain("deep.cs");
        text.ShouldContain("root.cs");
    }

    [Fact]
    public async Task ReturnsNoMatchesWhenNoneFound()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "text");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
            ["path"] = _tempDir,
        });

        text.ShouldBe("No matches found.");
    }

    [Fact]
    public async Task ReturnsErrorForMissingDirectory()
    {
        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
            ["path"] = Path.Combine(_tempDir, "nonexistent"),
        });

        text.ShouldContain("Error");
        text.ShouldContain("does not exist");
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
    public async Task ResultsSortedByModificationTimeDescending()
    {
        var file1 = Path.Combine(_tempDir, "old.cs");
        var file2 = Path.Combine(_tempDir, "new.cs");

        await File.WriteAllTextAsync(file1, "// old");
        File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddDays(-10));

        await File.WriteAllTextAsync(file2, "// new");
        File.SetLastWriteTimeUtc(file2, DateTime.UtcNow);

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
            ["path"] = _tempDir,
        });

        var idx1 = text.IndexOf("new.cs", StringComparison.Ordinal);
        var idx2 = text.IndexOf("old.cs", StringComparison.Ordinal);

        (idx1 < idx2).ShouldBeTrue("Newer file should appear before older file");
    }

    [Fact]
    public async Task MatchesSpecificFilePattern()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "README.md"), "# readme");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "notes.md"), "notes");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "code.cs"), "// code");

        var text = await InvokeAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "*.md",
            ["path"] = _tempDir,
        });

        text.ShouldContain("README.md");
        text.ShouldContain("notes.md");
        text.ShouldNotContain("code.cs");
    }
}
