using Shouldly;
using WeaveFleet.Application.Git;

namespace WeaveFleet.Application.Tests.Services;

public sealed class GitPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-gitpaths-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MainCheckoutOf_gives_the_checkout_for_a_folder_inside_it()
    {
        var repository = Path.Combine(_root, "weave-fleet");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "client", "src"));

        GitPaths.MainCheckoutOf(repository).ShouldBe(repository);
        GitPaths.MainCheckoutOf(Path.Combine(repository, "client", "src")).ShouldBe(repository);
    }

    [Fact]
    public void MainCheckoutOf_gives_a_linked_worktree_the_checkout_it_was_made_from()
    {
        var repository = Path.Combine(_root, "weave-fleet");
        var gitDir = Path.Combine(repository, ".git", "worktrees", "feature");
        Directory.CreateDirectory(gitDir);
        var worktree = Path.Combine(_root, "weave-fleet-worktrees", "feature");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");

        GitPaths.MainCheckoutOf(worktree).ShouldBe(repository);
        GitPaths.MainCheckoutOf(Path.Combine(worktree, "src")).ShouldBe(repository);
    }

    [Fact]
    public void MainCheckoutOf_gives_a_submodule_its_own_checkout()
    {
        var superproject = Path.Combine(_root, "super");
        Directory.CreateDirectory(Path.Combine(superproject, ".git", "modules", "lib"));
        var submodule = Path.Combine(superproject, "lib");
        Directory.CreateDirectory(submodule);
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");

        GitPaths.MainCheckoutOf(submodule).ShouldBe(submodule);
    }

    [Fact]
    public void MainCheckoutOf_is_null_outside_git()
    {
        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);

        // The temp folder could sit inside a checkout on some machines; only assert when it doesn't.
        if (GitPaths.MainCheckoutOf(_root) is null)
            GitPaths.MainCheckoutOf(folder).ShouldBeNull();
    }
}
