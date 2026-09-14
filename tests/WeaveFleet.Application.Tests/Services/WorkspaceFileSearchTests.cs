using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>The files and folders the composer's @ references are picked from.</summary>
public sealed class WorkspaceFileSearchTests
{
    [Fact]
    public async Task EmptyQuery_ListsTopLevel_FoldersFirst()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "README.md", "src/app.ts", "src/components/Button.vue", "docs/guide.md");

        var entries = await WorkspaceFileSearch.FindAsync(repository.Path, "", limit: 50);

        entries.ShouldBe(["docs/", "src/", "README.md"]);
    }

    [Fact]
    public async Task QueryEndingInSlash_ListsThatFolder()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "src/app.ts", "src/components/Button.vue", "src/components/icons/Plus.vue");

        var entries = await WorkspaceFileSearch.FindAsync(repository.Path, "src/", limit: 50);

        entries.ShouldBe(["src/components/", "src/app.ts"]);
    }

    [Fact]
    public async Task Search_FindsFolders_AndRanksNameMatchesAbovePathMatches()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "components.json", "src/components/Button.vue", "src/lib/components-helper.ts");

        var entries = (await WorkspaceFileSearch.FindAsync(repository.Path, "components", limit: 50)).ToList();

        entries[0].ShouldBe("src/components/");
        entries.ShouldContain("components.json");
        entries.ShouldContain("src/lib/components-helper.ts");
        entries.IndexOf("src/components/Button.vue").ShouldBeGreaterThan(entries.IndexOf("src/lib/components-helper.ts"));
    }

    [Fact]
    public async Task GitRepository_LeavesOutIgnoredFiles_ButKeepsUntrackedOnes()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, ".gitignore", "src/app.ts", "src/new-file.ts", "generated/app.g.ts", ".claude/worktrees/copy/src/app.ts");
        File.WriteAllText(Path.Combine(repository.Path, ".gitignore"), "generated/\n.claude/worktrees/\n");
        repository.Git("add", ".gitignore", "src/app.ts");

        var entries = await WorkspaceFileSearch.FindAsync(repository.Path, "app", limit: 50);

        entries.ShouldBe(["src/app.ts"]);
        (await WorkspaceFileSearch.FindAsync(repository.Path, "new-file", limit: 50)).ShouldBe(["src/new-file.ts"]);
    }

    [Fact]
    public async Task OutsideGit_WalksTheTree_SkippingDependencyAndBuildFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-search-{Guid.NewGuid():N}");
        try
        {
            Write(root, "src/app.ts", "node_modules/left-pad/app.js", "bin/Debug/app.dll", ".github/app.yml");

            var entries = await WorkspaceFileSearch.FindAsync(root, "app", limit: 50);

            entries.ShouldBe([".github/app.yml", "src/app.ts"], ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Limit_CapsTheResults()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, Enumerable.Range(1, 20).Select(i => $"notes/note-{i}.md").ToArray());

        var entries = await WorkspaceFileSearch.FindAsync(repository.Path, "note-", limit: 5);

        entries.Count.ShouldBe(5);
    }

    private static void Write(string root, params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relativePath);
        }
    }
}
