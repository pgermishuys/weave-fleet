using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Services.Worktrees;
using WeaveFleet.Domain.Common;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services.Worktrees;

/// <summary>
/// Worktree naming end to end against real git: the templates decide the branch and the folder,
/// and the server does the naming, so a session started without a branch is named from its message.
/// </summary>
public sealed class WorkspaceServiceWorktreeNamingTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();

    private WorkspaceService Service() => new(
        _workspaces,
        new TestUserContext(),
        new FleetOptions(),
        NullLogger<WorkspaceService>.Instance,
        new WorktreeNamingService(_preferences, NullLogger<WorktreeNamingService>.Instance));

    [Fact]
    public async Task WithNoBranchChosen_TheMessageNamesTheBranch()
    {
        using var repository = new RealGitRepository();

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "Add rate limiting to the session create endpoint");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("fleet/add-rate-limiting-session-create");
        Path.GetFileName(result.Value.Directory).ShouldBe("fleet-add-rate-limiting-session-create");
    }

    [Fact]
    public async Task APrefixOfYourOwn_NamesTheBranchAndTheFolder()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride
        {
            Branch = "{prefix}/{slug}",
            Prefix = "pg",
        });

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "Add rate limiting");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("pg/add-rate-limiting");
        Path.GetFileName(result.Value.Directory).ShouldBe("pg-add-rate-limiting");
    }

    [Fact]
    public async Task TheRepositorysOwnConvention_BeatsTheUsers()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride { Branch = "{prefix}/{slug}", Prefix = "pg" });
        await WriteProjectConfigAsync(repository.Path, """
            {
              // Everyone on this repository names branches the same way.
              "worktrees": {
                "branch": "feature/{ticket}-{slug}",
                "capture": { "ticket": "[A-Z]{2,}-\\d+" }
              }
            }
            """);

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "PLAT-1841 add rate limiting");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("feature/PLAT-1841-add-rate-limiting");
    }

    [Fact]
    public async Task ARepositoryConventionThatCannotName_IsIgnored()
    {
        using var repository = new RealGitRepository();
        await WriteProjectConfigAsync(repository.Path, """
            { "worktrees": { "branch": "feature/{nope}" } }
            """);

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "Add rate limiting");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("fleet/add-rate-limiting");
    }

    [Fact]
    public async Task AMessageWithNoSlugInIt_FallsBackToASessionName()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride { Branch = "{prefix}/{slug}", Prefix = "pg" });

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null, message: "🚀🚀🚀");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        // Not "pg", which is what the template alone would have collapsed to.
        result.Value.Branch.ShouldStartWith("weave-session-");
        Path.GetFileName(result.Value.Directory).ShouldBe(result.Value.Branch);
    }

    [Fact]
    public async Task ATypedBranch_StillWinsOverTheTemplate()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride { Branch = "{prefix}/{slug}", Prefix = "pg" });

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: "hotfix/urgent", provenance: null,
            message: "Add rate limiting");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Branch.ShouldBe("hotfix/urgent");
        Path.GetFileName(result.Value.Directory).ShouldBe("hotfix-urgent");
    }

    [Fact]
    public async Task ARootTemplate_PutsTheWorktreeSomewhereElse()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride
        {
            Root = "{repoParent}/all-worktrees/{repo}",
            Folder = "{slug}",
        });

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "Add rate limiting");

        result.IsSuccess.ShouldBeTrue(ErrorOf(result));
        result.Value.Directory.ShouldBe(
            Path.Combine(repository.ParentPath, "all-worktrees", "repo", "add-rate-limiting"));
        Directory.Exists(result.Value.Directory).ShouldBeTrue();
    }

    [Fact]
    public async Task CleaningUpTheLastWorktree_RemovesACustomRootToo()
    {
        using var repository = new RealGitRepository();
        await SaveUserNamingAsync(new WorktreeNamingOverride
        {
            Root = "{repoParent}/all-worktrees/{repo}",
            Folder = "{slug}",
        });
        var service = Service();
        var created = await service.CreateWorkspaceAsync(
            repository.Path, "worktree", branch: null, provenance: null,
            message: "Add rate limiting");
        created.IsSuccess.ShouldBeTrue(ErrorOf(created));

        var cleanup = await service.CleanupWorkspaceAsync(created.Value.Id);

        cleanup.IsSuccess.ShouldBeTrue();
        Directory.Exists(Path.Combine(repository.ParentPath, "all-worktrees", "repo")).ShouldBeFalse();
    }

    private async Task SaveUserNamingAsync(WorktreeNamingOverride layer)
    {
        var naming = new WorktreeNamingService(_preferences, NullLogger<WorktreeNamingService>.Instance);
        var saved = await naming.SaveUserAsync(layer);
        saved.IsSuccess.ShouldBeTrue(saved.IsFailure ? saved.Error.Description : null);
    }

    private static Task WriteProjectConfigAsync(string repositoryPath, string json)
        => File.WriteAllTextAsync(
            Path.Combine(repositoryPath, WorktreeNamingService.ProjectConfigFileName), json);

    private static string? ErrorOf<T>(Result<T> result)
        => result.IsFailure ? result.Error.Description : null;
}
