using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.SessionSources;

namespace WeaveFleet.Infrastructure.Tests.SessionSources;

public sealed class RepositorySessionSourceProviderTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsWorkspaceIntentForAllowedRepository()
    {
        using var repository = new GitRepositoryFixture();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = repository.ParentPath,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);
        var provider = new RepositorySessionSourceProvider(repositoryService);

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = SessionSourceCatalog.RepositoryStartSession.Key,
            Input = JsonSerializer.SerializeToElement(new
            {
                repositoryPath = repository.Path,
                isolationStrategy = "worktree",
                branch = "feature/source-provider"
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.Directory.ShouldBe(WorkspaceRootService.CanonicalizePath(repository.Path));
        result.Value.Input.WorkspaceIntent.IsolationStrategy.ShouldBe("worktree");
        result.Value.Input.WorkspaceIntent.Branch.ShouldBe("feature/source-provider");
        result.Value.Input.Provenance.ProviderId.ShouldBe(SessionSourceProviderIds.Repository);
        result.Value.Input.Provenance.SourceType.ShouldBe(SessionSourceTypeNames.Repository);
    }

    [Fact]
    public async Task ResolveAsync_RejectsCloneIsolationStrategy()
    {
        using var repository = new GitRepositoryFixture();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = repository.ParentPath,
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceRootRepository);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository));
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var repositoryService = new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance);
        var provider = new RepositorySessionSourceProvider(repositoryService);

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = SessionSourceCatalog.RepositoryStartSession.Key,
            Input = JsonSerializer.SerializeToElement(new
            {
                repositoryPath = repository.Path,
                isolationStrategy = "clone"
            })
        }, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("only 'existing' and 'worktree'");
    }

    private sealed class GitRepositoryFixture : IDisposable
    {
        public GitRepositoryFixture()
        {
            ParentPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-root-{Guid.NewGuid():N}");
            Path = System.IO.Path.Combine(ParentPath, "repo");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        }

        public string ParentPath { get; }
        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(ParentPath))
                Directory.Delete(ParentPath, recursive: true);
        }
    }
}
