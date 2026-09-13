using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Worktree creation against real git repositories.</summary>
public sealed class WorkspaceServiceWorktreeTests
{
    private readonly WorkspaceService _service = new(
        new InMemoryWorkspaceRepository(),
        new TestUserContext(),
        new FleetOptions(),
        NullLogger<WorkspaceService>.Instance);

    [Fact]
    public async Task NewWorktree_StartsFromMain_NotTheBranchTheCheckoutIsOn()
    {
        using var repository = new RealGitRepository();
        var mainCommit = repository.CommitOf("main");
        repository.Git("checkout", "-b", "feature/half-done");
        repository.Git("commit", "--allow-empty", "-m", "unfinished work");

        var result = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/fix-login");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(mainCommit);
        result.Value.Branch.ShouldBe("fleet/fix-login");
    }

    [Fact]
    public async Task NewWorktree_StartsFromFreshlyFetchedOriginMain_WithoutTrackingIt()
    {
        using var repository = new RealGitRepository();
        var pushToOrigin = repository.AddOrigin();
        var newerOriginCommit = pushToOrigin("landed on origin after our last fetch");

        var result = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/from-origin");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(newerOriginCommit);
        Should.Throw<InvalidOperationException>(() => repository.Git("config", "branch.fleet/from-origin.merge"));
    }

    [Fact]
    public async Task BranchCheckedOutInAnotherWorktree_GetsASuffix()
    {
        using var repository = new RealGitRepository();

        var first = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/fix-login");
        var second = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/fix-login");

        first.IsSuccess.ShouldBeTrue(ErrorOf(first));
        second.IsSuccess.ShouldBeTrue(ErrorOf(second));
        second.Value.Branch.ShouldBe("fleet/fix-login-2");
        second.Value.Directory.ShouldNotBe(first.Value.Directory);
        BranchIn(repository, second.Value.Directory).ShouldBe("fleet/fix-login-2");
    }

    [Fact]
    public async Task ExistingBranchNotCheckedOut_IsCheckedOutAsIs()
    {
        using var repository = new RealGitRepository();
        repository.Git("branch", "fleet/earlier-work");
        repository.Git("checkout", "fleet/earlier-work");
        repository.Git("commit", "--allow-empty", "-m", "earlier work");
        var earlierCommit = repository.CommitOf("HEAD");
        repository.Git("checkout", "main");

        var result = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/earlier-work");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("fleet/earlier-work");
        CommitIn(repository, result.Value.Directory).ShouldBe(earlierCommit);
    }

    [Fact]
    public async Task LeftoverFolder_GetsASuffixedFolder()
    {
        using var repository = new RealGitRepository();
        var leftover = Path.Combine(repository.ParentPath, "repo-worktrees", "fleet-leftover");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "notes.txt"), "not a worktree");

        var result = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/leftover");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Directory.ShouldBe($"{leftover}-2");
        result.Value.Branch.ShouldBe("fleet/leftover");
        File.Exists(Path.Combine(leftover, "notes.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task WorktreeFolderDeletedByHand_DoesNotBlockItsBranch()
    {
        using var repository = new RealGitRepository();
        var first = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/deleted");
        first.IsSuccess.ShouldBeTrue(ErrorOf(first));
        Directory.Delete(first.Value.Directory, recursive: true);

        var second = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "fleet/deleted");

        second.IsSuccess.ShouldBeTrue(ErrorOf(second));
        second.Value.Branch.ShouldBe("fleet/deleted");
        second.Value.Directory.ShouldBe(first.Value.Directory);
    }

    [Fact]
    public async Task GitFailure_IsAReadableValidationError()
    {
        using var repository = new RealGitRepository();

        var result = await _service.CreateWorkspaceAsync(repository.Path, "worktree", "bad..name");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldBe("Couldn't create the worktree: 'bad..name' is not a valid branch name");
    }

    private static string CommitIn(RealGitRepository repository, string worktreeDirectory) =>
        repository.Git("-C", worktreeDirectory, "rev-parse", "HEAD").Trim();

    private static string BranchIn(RealGitRepository repository, string worktreeDirectory) =>
        repository.Git("-C", worktreeDirectory, "branch", "--show-current").Trim();

    private static string ErrorOf<T>(WeaveFleet.Domain.Common.Result<T> result) =>
        result.IsFailure ? $"Expected success but got: {result.Error.Description}" : string.Empty;
}
