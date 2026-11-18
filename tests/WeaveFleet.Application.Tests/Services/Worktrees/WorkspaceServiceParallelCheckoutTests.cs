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
/// A new worktree is checked out on every core (git only goes parallel past 100 files), and every file still lands.
/// </summary>
public sealed class WorkspaceServiceParallelCheckoutTests
{
    private static WorkspaceService Service() => new(
        new InMemoryWorkspaceRepository(),
        new TestUserContext(),
        new FleetOptions(),
        NullLogger<WorkspaceService>.Instance,
        new WorktreeNamingService(new InMemoryUserPreferenceRepository(), NullLogger<WorktreeNamingService>.Instance));

    private static void AddFiles(RealGitRepository repository, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var folder = Path.Combine(repository.Path, $"dir{i % 7}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"file{i}.txt"), $"content {i}");
        }

        repository.Git("add", "-A");
        repository.Git("commit", "-q", "-m", "many files");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public async Task ABigRepositorysWorktree_HasEveryFile(string? userWorkers)
    {
        using var repository = new RealGitRepository();
        AddFiles(repository, 400);
        if (userWorkers is not null)
            repository.Git("config", "checkout.workers", userWorkers);

        var result = await Service().CreateWorkspaceAsync(
            repository.Path, "worktree", branch: "feature/big", provenance: null, message: null);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.ToString());
        for (var i = 0; i < 400; i += 37)
            File.ReadAllText(Path.Combine(result.Value.Directory, $"dir{i % 7}", $"file{i}.txt")).ShouldBe($"content {i}");
    }
}
