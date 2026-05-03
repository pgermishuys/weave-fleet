using System.Text.Json;
using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class MultiEditToolTests : IDisposable
{
    private readonly MultiEditTool _sut = new();
    private readonly string _testDir;

    public MultiEditToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "nucode-multiedit-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    // ── Basic properties ──

    [Fact]
    public void NameIsMultiedit()
    {
        _sut.Name.ShouldBe("multiedit");
    }

    [Fact]
    public void ToAIFunctionReturnsFunction()
    {
        var fn = _sut.ToAIFunction();
        fn.ShouldNotBeNull();
        fn.Name.ShouldBe("multiedit");
    }

    // ── Validation ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyFilePathReturnsError(string? filePath)
    {
        var result = await InvokeAsync(filePath: filePath!, editsJson: "[]");
        result.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task NonArrayEditsReturnsError()
    {
        var file = CreateTestFile("content");
        var result = await InvokeAsync(filePath: file, editsJson: "\"not-an-array\"");
        result.ShouldContain("edits must be an array");
    }

    [Fact]
    public async Task EmptyEditsArrayReturnsError()
    {
        var file = CreateTestFile("content");
        var result = await InvokeAsync(filePath: file, editsJson: "[]");
        result.ShouldContain("must not be empty");
    }

    [Fact]
    public async Task MissingOldStringReturnsError()
    {
        var file = CreateTestFile("content");
        var result = await InvokeAsync(filePath: file, editsJson: """[{"newString": "replacement"}]""");
        result.ShouldContain("must have an 'oldString'");
    }

    [Fact]
    public async Task MissingNewStringReturnsError()
    {
        var file = CreateTestFile("content");
        var result = await InvokeAsync(filePath: file, editsJson: """[{"oldString": "content"}]""");
        result.ShouldContain("must have a 'newString'");
    }

    [Fact]
    public async Task FileNotFoundReturnsError()
    {
        var result = await InvokeAsync(
            filePath: Path.Combine(_testDir, "nonexistent.txt"),
            editsJson: """[{"oldString": "a", "newString": "b"}]""");
        result.ShouldContain("File not found");
    }

    [Fact]
    public async Task DirectoryPathReturnsError()
    {
        var result = await InvokeAsync(
            filePath: _testDir,
            editsJson: """[{"oldString": "a", "newString": "b"}]""");
        result.ShouldContain("directory");
    }

    // ── Single edit ──

    [Fact]
    public async Task SingleEditReplacesText()
    {
        var file = CreateTestFile("Hello World");

        var result = await InvokeAsync(filePath: file,
            editsJson: """[{"oldString": "World", "newString": "NuCode"}]""");

        result.ShouldContain("1 edits applied successfully");
        (await File.ReadAllTextAsync(file)).ShouldBe("Hello NuCode");
    }

    // ── Multiple sequential edits ──

    [Fact]
    public async Task MultipleEditsAppliedSequentially()
    {
        var file = CreateTestFile("AAA BBB CCC");

        var edits = """
        [
            {"oldString": "AAA", "newString": "XXX"},
            {"oldString": "BBB", "newString": "YYY"},
            {"oldString": "CCC", "newString": "ZZZ"}
        ]
        """;
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("3 edits applied successfully");
        (await File.ReadAllTextAsync(file)).ShouldBe("XXX YYY ZZZ");
    }

    [Fact]
    public async Task LaterEditSeesResultOfEarlierEdit()
    {
        var file = CreateTestFile("Hello World");

        var edits = """
        [
            {"oldString": "World", "newString": "Planet"},
            {"oldString": "Planet", "newString": "Universe"}
        ]
        """;
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("2 edits applied successfully");
        (await File.ReadAllTextAsync(file)).ShouldBe("Hello Universe");
    }

    // ── Error in sequence ──

    [Fact]
    public async Task ErrorInSecondEditStopsAndReportsIndex()
    {
        var file = CreateTestFile("Hello World");

        var edits = """
        [
            {"oldString": "Hello", "newString": "Hi"},
            {"oldString": "NOTFOUND", "newString": "replacement"}
        ]
        """;
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("Error in edit 1");
        result.ShouldContain("not found");
        // File should NOT be changed — we read then write at the end
        // Actually our impl reads once, applies all, writes at end — so if edit 1 succeeds
        // but edit 2 fails, we never write. The original content remains.
        (await File.ReadAllTextAsync(file)).ShouldBe("Hello World");
    }

    [Fact]
    public async Task IdenticalOldAndNewStringReturnsError()
    {
        var file = CreateTestFile("Hello World");

        var edits = """[{"oldString": "Hello", "newString": "Hello"}]""";
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("Error in edit 0");
        result.ShouldContain("identical");
    }

    // ── replaceAll ──

    [Fact]
    public async Task ReplaceAllReplacesAllOccurrences()
    {
        var file = CreateTestFile("foo bar foo baz foo");

        var edits = """[{"oldString": "foo", "newString": "qux", "replaceAll": true}]""";
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("1 edits applied successfully");
        (await File.ReadAllTextAsync(file)).ShouldBe("qux bar qux baz qux");
    }

    [Fact]
    public async Task MultipleMatchesWithoutReplaceAllReturnsError()
    {
        var file = CreateTestFile("foo bar foo");

        var edits = """[{"oldString": "foo", "newString": "qux"}]""";
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("Error in edit 0");
        result.ShouldContain("2 matches");
    }

    // ── Line ending normalization ──

    [Fact]
    public async Task LineEndingNormalizationPreservesCrlf()
    {
        var file = CreateTestFile("line1\r\nline2\r\nline3");

        // Search with \n — should still match because of normalization
        var edits = """[{"oldString": "line1\nline2", "newString": "merged"}]""";
        var result = await InvokeAsync(filePath: file, editsJson: edits);

        result.ShouldContain("1 edits applied successfully");
        var content = await File.ReadAllTextAsync(file);
        content.ShouldBe("merged\r\nline3");
    }

    // ── Helpers ──

    private string CreateTestFile(string content)
    {
        var path = Path.Combine(_testDir, $"test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<string> InvokeAsync(string filePath, string editsJson)
    {
        var fn = _sut.ToAIFunction();
        var editsElement = JsonSerializer.Deserialize<JsonElement>(editsJson);

        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["edits"] = editsElement,
        };

        var result = await fn.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
        return result?.ToString() ?? "";
    }
}
