using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Application.Workspaces.Worktrees;
using WeaveFleet.Domain.Common;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services.Worktrees;

/// <summary>
/// Git keeps branches as files, so <c>fix</c> and <c>fix/login</c> can't both exist ("cannot lock ref").
/// A new session whose branch clashes that way gets a free name, the way an exact clash gets "-2".
/// </summary>
public sealed class WorkspaceServiceBranchClashTests
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
    public async Task ABranchUnderAnExistingBranchsName_GetsAFreeName()
    {
        using var repository = new RealGitRepository();
        repository.Git("branch", "fix");

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: "fix/login", provenance: null, message: null);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.ToString());
        result.Value.Branch.ShouldBe("fix-2/login");
    }

    [Fact]
    public async Task ABranchThatAnExistingBranchLivesUnder_GetsAFreeName()
    {
        using var repository = new RealGitRepository();
        repository.Git("branch", "fix/login");

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: "fix", provenance: null, message: null);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.ToString());
        result.Value.Branch.ShouldBe("fix-2");
    }

    [Fact]
    public async Task ARemoteOnlyBranchOfTheSameName_DoesNotClash()
    {
        using var repository = new RealGitRepository();
        repository.AddOrigin();
        repository.PushToOrigin("remote fix", "fix");
        repository.Git("fetch", "origin");

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: "fix/login", provenance: null, message: null);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.ToString());
        result.Value.Branch.ShouldBe("fix/login");
    }
}
