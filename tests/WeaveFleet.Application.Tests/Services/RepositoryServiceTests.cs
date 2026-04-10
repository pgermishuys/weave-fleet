using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class RepositoryServiceTests
{
    [Fact]
    public async Task ResolveRepositoryPathAsync_ReturnsFailureWhenOutsideAllowedRoots()
    {
        using var allowedRoot = new TempDirectory();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = allowedRoot.Path,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));

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
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = tempDirectory.Path,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));

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
        using var allowedRoot = new TempDirectory();
        using var outsideRepository = new TempDirectory(createGitDirectory: true);
        var symlinkPath = System.IO.Path.Combine(allowedRoot.Path, "repo-link");

        Directory.CreateSymbolicLink(symlinkPath, outsideRepository.Path);

        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = allowedRoot.Path,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));

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
        using var allowedRoot = new TempDirectory();
        using var outsideParent = new TempDirectory();
        var outsideRepositoryPath = System.IO.Path.Combine(outsideParent.Path, "nested-repo");
        Directory.CreateDirectory(outsideRepositoryPath);
        Directory.CreateDirectory(System.IO.Path.Combine(outsideRepositoryPath, ".git"));

        var symlinkDirectory = System.IO.Path.Combine(allowedRoot.Path, "link-outside");
        Directory.CreateSymbolicLink(symlinkDirectory, outsideParent.Path);

        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = allowedRoot.Path,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);

        var nestedPath = System.IO.Path.Combine(symlinkDirectory, "nested-repo");
        var result = await repositoryService.ResolveRepositoryPathAsync(nestedPath);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside allowed workspace roots");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(bool createGitDirectory = false)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-{Guid.NewGuid():N}");
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
