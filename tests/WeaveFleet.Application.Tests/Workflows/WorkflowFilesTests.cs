using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowFilesTests
{
    private readonly WorkflowFiles _files = new();

    [Fact]
    public void a_declared_file_counts_when_its_a_file_in_the_worktree()
    {
        using var worktree = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(worktree.Path, "docs", "design"));
        File.WriteAllText(Path.Combine(worktree.Path, "docs", "design", "sheet.md"), "# Sheet");
        Directory.CreateDirectory(Path.Combine(worktree.Path, "docs", "design", "sheet.html"));

        _files.Missing(worktree.Path, ["docs/design/sheet.md", "docs/design/sheet.html", "docs/design/other.md"])
            .ShouldBe(["docs/design/sheet.html", "docs/design/other.md"]);
    }

    [Fact]
    public void nothing_is_there_before_the_worktree_is()
        => _files.Missing(null, ["a.md"]).ShouldBe(["a.md"]);

    [Fact]
    public void a_path_that_leaves_the_worktree_counts_as_missing()
    {
        using var worktree = new TempDirectory();
        var outside = Path.Combine(Path.GetDirectoryName(worktree.Path)!, $"outside-{Guid.NewGuid():N}.md");
        File.WriteAllText(outside, "x");
        try
        {
            _files.Missing(worktree.Path, [$"../{Path.GetFileName(outside)}"]).ShouldHaveSingleItem();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-workflow-files-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
