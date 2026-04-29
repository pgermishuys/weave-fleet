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
