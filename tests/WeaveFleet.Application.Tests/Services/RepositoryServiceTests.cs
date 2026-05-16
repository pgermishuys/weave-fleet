using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class RepositoryServiceTests
{
    private static bool CanCreateDirectorySymlink()
    {
        using var target = new TempDirectory();
        using var parent = new TempDirectory();
        var symlinkPath = System.IO.Path.Combine(parent.Path, "symlink-check");

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, target.Path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(symlinkPath))
                    Directory.Delete(symlinkPath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ResolveRepositoryPathAsync_ReturnsFailureWhenOutsideAllowedRoots()
    {
        using var allowedRoot = new TempDirectory();
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = allowedRoot.Path,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var userContext = new TestUserContext();
        var services = new ServiceCollection();
        services.AddSingleton<WeaveFleet.Domain.Repositories.IWorkspaceRootRepository>(workspaceRootRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        using var tempDirectory = new TempDirectory(createGitDirectory: true);

        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);
        var result = await repositoryService.ResolveRepositoryPathAsync(tempDirectory.Path);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside allowed workspace roots");
    }

    [Fact]
    public async Task ResolveRepositoryPathAsync_ReturnsFailureWhenPathIsNotGitRepository()
    {
        using var tempDirectory = new TempDirectory();
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = tempDirectory.Path,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var userContext = new TestUserContext();
        var services = new ServiceCollection();
        services.AddSingleton<WeaveFleet.Domain.Repositories.IWorkspaceRootRepository>(workspaceRootRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);

        var result = await repositoryService.ResolveRepositoryPathAsync(tempDirectory.Path);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("not a git repository");
    }

    [Fact]
    public async Task ResolveRepositoryPathAsync_ReturnsFailureWhenSymlinkEscapesAllowedRoots()
    {
        if (!CanCreateDirectorySymlink())
            return;

        using var allowedRoot = new TempDirectory();
        using var outsideRepository = new TempDirectory(createGitDirectory: true);
        var symlinkPath = System.IO.Path.Combine(allowedRoot.Path, "repo-link");

        Directory.CreateSymbolicLink(symlinkPath, outsideRepository.Path);

        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = allowedRoot.Path,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var userContext = new TestUserContext();
        var services = new ServiceCollection();
        services.AddSingleton<WeaveFleet.Domain.Repositories.IWorkspaceRootRepository>(workspaceRootRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);

        var result = await repositoryService.ResolveRepositoryPathAsync(symlinkPath);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside allowed workspace roots");
    }

    [Fact]
    public async Task ResolveRepositoryPathAsync_ReturnsFailureWhenNestedSymlinkEscapesAllowedRoots()
    {
        if (!CanCreateDirectorySymlink())
            return;

        using var allowedRoot = new TempDirectory();
        using var outsideParent = new TempDirectory();
        var outsideRepositoryPath = System.IO.Path.Combine(outsideParent.Path, "nested-repo");
        Directory.CreateDirectory(outsideRepositoryPath);
        Directory.CreateDirectory(System.IO.Path.Combine(outsideRepositoryPath, ".git"));

        var symlinkDirectory = System.IO.Path.Combine(allowedRoot.Path, "link-outside");
        Directory.CreateSymbolicLink(symlinkDirectory, outsideParent.Path);

        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = allowedRoot.Path,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var userContext = new TestUserContext();
        var services = new ServiceCollection();
        services.AddSingleton<WeaveFleet.Domain.Repositories.IWorkspaceRootRepository>(workspaceRootRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);

        var nestedPath = System.IO.Path.Combine(symlinkDirectory, "nested-repo");
        var result = await repositoryService.ResolveRepositoryPathAsync(nestedPath);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside allowed workspace roots");
    }

    [Fact]
    public void parse_worktrees_returns_linked_worktrees_only()
    {
        var porcelain = "worktree /repos/main\nHEAD abc123\nbranch refs/heads/main\n\nworktree /repos/main-worktrees/feature-auth\nHEAD def456\nbranch refs/heads/feature/auth\n\nworktree /repos/main-worktrees/hotfix\nHEAD 789abc\nbranch refs/heads/hotfix-1\n";

        var result = RepositoryService.ParseWorktrees(porcelain, "/repos/main");

        result.Count.ShouldBe(2);
        result[0].Path.ShouldBe("/repos/main-worktrees/feature-auth");
        result[0].Branch.ShouldBe("feature/auth");
        result[0].CommitHash.ShouldBe("def456");
        result[1].Path.ShouldBe("/repos/main-worktrees/hotfix");
        result[1].Branch.ShouldBe("hotfix-1");
    }

    [Fact]
    public void parse_worktrees_returns_empty_when_only_main_worktree_present()
    {
        var porcelain = "worktree /repos/main\nHEAD abc123\nbranch refs/heads/main\n";

        var result = RepositoryService.ParseWorktrees(porcelain, "/repos/main");

        result.Count.ShouldBe(0);
    }

    [Fact]
    public void parse_worktrees_handles_detached_head()
    {
        var porcelain = "worktree /repos/main\nHEAD abc123\nbranch refs/heads/main\n\nworktree /repos/main-worktrees/detached\nHEAD cafe00\ndetached\n";

        var result = RepositoryService.ParseWorktrees(porcelain, "/repos/main");

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("/repos/main-worktrees/detached");
        result[0].Branch.ShouldBeNull();
        result[0].CommitHash.ShouldBe("cafe00");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(bool createGitDirectory = false)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (createGitDirectory)
                Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
