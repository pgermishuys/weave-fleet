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

    [Fact]
    public async Task ChosenOriginBase_StartsFromThatBranchFreshlyFetched()
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();
        repository.PushToOrigin("release branch exists", "release/2.0");
        repository.Git("fetch", "origin");
        var newerReleaseCommit = repository.PushToOrigin("landed on release after our last fetch", "release/2.0");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/hotfix", provenance: null, baseBranch: "origin/release/2.0");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(newerReleaseCommit);
        result.Value.Branch.ShouldBe("fleet/hotfix");
        Should.Throw<InvalidOperationException>(() => repository.Git("config", "branch.fleet/hotfix.merge"));
    }

    [Fact]
    public async Task ChosenOriginBase_OnlyOnOrigin_IsFetchedFirst()
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();
        var releaseCommit = repository.PushToOrigin("never fetched here", "release/3.0");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/from-new-release", provenance: null, baseBranch: "origin/release/3.0");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(releaseCommit);
    }

    [Fact]
    public async Task FetchOff_StartsFromTheLastFetchedCopy()
    {
        using var repository = new RealGitRepository();
        var pushToOrigin = repository.AddOrigin();
        var lastFetched = repository.CommitOf("origin/main");
        pushToOrigin("landed on origin after our last fetch");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/offline", provenance: null, baseBranch: null, fetchOrigin: false);

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(lastFetched);
    }

    [Fact]
    public async Task FetchOff_WithAChosenOriginBase_StartsFromItsLastFetchedCopy()
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();
        repository.PushToOrigin("release branch exists", "release/2.0");
        repository.Git("fetch", "origin");
        var lastFetched = repository.CommitOf("origin/release/2.0");
        repository.PushToOrigin("landed on release after our last fetch", "release/2.0");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/offline", provenance: null, baseBranch: "origin/release/2.0", fetchOrigin: false);

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(lastFetched);
    }

    [Fact]
    public async Task ChosenLocalBase_StartsFromThatBranch_EvenWhenTheCheckoutIsElsewhere()
    {
        using var repository = new RealGitRepository();
        repository.Git("checkout", "-b", "feature/half-done");
        repository.Git("commit", "--allow-empty", "-m", "unfinished work");
        var featureCommit = repository.CommitOf("HEAD");
        repository.Git("checkout", "main");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/continue", provenance: null, baseBranch: "feature/half-done");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        CommitIn(repository, result.Value.Directory).ShouldBe(featureCommit);
        BranchIn(repository, result.Value.Directory).ShouldBe("fleet/continue");
    }

    [Fact]
    public async Task ChosenBase_NeverReusesAnExistingBranch()
    {
        using var repository = new RealGitRepository();
        repository.Git("branch", "fleet/earlier-work");
        repository.Git("checkout", "-b", "develop");
        repository.Git("commit", "--allow-empty", "-m", "develop work");
        var developCommit = repository.CommitOf("HEAD");
        repository.Git("checkout", "main");

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/earlier-work", provenance: null, baseBranch: "develop");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("fleet/earlier-work-2");
        CommitIn(repository, result.Value.Directory).ShouldBe(developCommit);
    }

    [Theory]
    [InlineData("origin/no-such-branch")]
    [InlineData("no-such-branch")]
    public async Task UnknownBase_IsAReadableValidationError_AndCreatesNothing(string baseBranch)
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/nowhere", provenance: null, baseBranch: baseBranch);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        result.Error.Description.ShouldBe($"Couldn't create the worktree: there's no branch {baseBranch} to start from");
        Directory.Exists(Path.Combine(repository.ParentPath, "repo-worktrees")).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => repository.Git("show-ref", "--verify", "refs/heads/fleet/nowhere"));
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("main:refs/heads/main")]
    [InlineData("bad..name")]
    [InlineData("has space")]
    public async Task InvalidBaseName_IsRejectedBeforeGitSeesIt(string baseBranch)
    {
        using var repository = new RealGitRepository();

        var result = await _service.CreateWorkspaceAsync(
            repository.Path, "worktree", "fleet/x", provenance: null, baseBranch: baseBranch);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe($"'{baseBranch}' is not a valid branch name.");
    }

    [Fact]
    public async Task DefaultBase_IsOriginsDefault_WhenThereIsAnOrigin()
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();
        repository.Git("checkout", "-b", "feature/elsewhere");

        var defaults = await WorkspaceService.ResolveDefaultBaseAsync(repository.Path);

        defaults.ShouldBe(new DefaultWorktreeBase("main", "origin/main"));
    }

    [Fact]
    public async Task DefaultBase_IsLocalMain_WithoutAnOrigin()
    {
        using var repository = new RealGitRepository();

        var defaults = await WorkspaceService.ResolveDefaultBaseAsync(repository.Path);

        defaults.ShouldBe(new DefaultWorktreeBase("main", "main"));
    }

    [Fact]
    public async Task DefaultBase_IsNothing_WithoutOriginMainOrMaster()
    {
        using var repository = new RealGitRepository();
        repository.Git("branch", "-m", "main", "trunk");

        var defaults = await WorkspaceService.ResolveDefaultBaseAsync(repository.Path);

        defaults.ShouldBe(new DefaultWorktreeBase(null, null));
    }

    private static string CommitIn(RealGitRepository repository, string worktreeDirectory) =>
        repository.Git("-C", worktreeDirectory, "rev-parse", "HEAD").Trim();

    private static string BranchIn(RealGitRepository repository, string worktreeDirectory) =>
        repository.Git("-C", worktreeDirectory, "branch", "--show-current").Trim();

    private static string ErrorOf<T>(WeaveFleet.Domain.Common.Result<T> result) =>
        result.IsFailure ? $"Expected success but got: {result.Error.Description}" : string.Empty;
}
