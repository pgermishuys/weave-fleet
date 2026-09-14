using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.SessionSources;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Infrastructure.Tests.SessionSources;

public sealed class RepositorySessionSourceProviderTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsWorkspaceIntentForAllowedRepository()
    {
        using var repository = new GitRepositoryFixture();
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = repository.ParentPath,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(workspaceRootRepository);
        var userContext = new TestUserContext();
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));
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
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = repository.ParentPath,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(workspaceRootRepository);
        var userContext = new TestUserContext();
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));
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

    [Fact]
    public async Task ResolveAsync_RejectsUnknownExistingWorktreePath()
    {
        using var repository = new GitRepositoryFixture();
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = repository.ParentPath,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(workspaceRootRepository);
        var userContext = new TestUserContext();
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));
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
                existingWorktreePath = "/not/a/known/worktree"
            })
        }, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("not a known worktree");
    }

    [Fact]
    public async Task ResolveAsync_AcceptsExistingWorktreePath_WhenValid()
    {
        using var repository = new RealGitRepository();
        var worktreePath = repository.CreateWorktree("feature-test");

        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = repository.ParentPath,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(workspaceRootRepository);
        var userContext = new TestUserContext();
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));
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
                existingWorktreePath = worktreePath
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue($"Expected success but got: {(result.IsFailure ? result.Error.Description : "")}");
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.IsolationStrategy.ShouldBe("existing");
        result.Value.Input.WorkspaceIntent.Branch.ShouldBe("feature-test");
    }

    [Fact]
    public async Task ResolveAsync_PassesTheChosenBaseAndFetch_ForANewWorktree()
    {
        using var repository = new GitRepositoryFixture();
        var provider = CreateProvider(repository.ParentPath);

        var result = await provider.ResolveAsync(RepositorySelection(new
        {
            repositoryPath = repository.Path,
            isolationStrategy = "worktree",
            branch = "fleet/hotfix",
            baseBranch = " origin/release/2.0 ",
            fetchOrigin = false
        }), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue($"Expected success but got: {(result.IsFailure ? result.Error.Description : "")}");
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.BaseBranch.ShouldBe("origin/release/2.0");
        result.Value.Input.WorkspaceIntent.FetchOrigin.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_DefaultsToTheRepositoryDefaultAndFetching()
    {
        using var repository = new GitRepositoryFixture();
        var provider = CreateProvider(repository.ParentPath);

        var result = await provider.ResolveAsync(RepositorySelection(new
        {
            repositoryPath = repository.Path,
            isolationStrategy = "worktree"
        }), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.WorkspaceIntent!.BaseBranch.ShouldBeNull();
        result.Value.Input.WorkspaceIntent.FetchOrigin.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_RejectsABase_ForTheCurrentCheckout()
    {
        using var repository = new GitRepositoryFixture();
        var provider = CreateProvider(repository.ParentPath);

        var result = await provider.ResolveAsync(RepositorySelection(new
        {
            repositoryPath = repository.Path,
            isolationStrategy = "existing",
            baseBranch = "origin/main"
        }), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("A base branch can only be chosen for a new worktree.");
    }

    [Fact]
    public async Task ResolveAsync_RejectsABaseGitWouldReadAsAnOption()
    {
        using var repository = new GitRepositoryFixture();
        var provider = CreateProvider(repository.ParentPath);

        var result = await provider.ResolveAsync(RepositorySelection(new
        {
            repositoryPath = repository.Path,
            isolationStrategy = "worktree",
            baseBranch = "--upload-pack=touch /tmp/x"
        }), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
    }

    private static SessionSourceSelection RepositorySelection(object input) => new()
    {
        Key = SessionSourceCatalog.RepositoryStartSession.Key,
        Input = JsonSerializer.SerializeToElement(input)
    };

    private static RepositorySessionSourceProvider CreateProvider(string workspaceRoot)
    {
        var workspaceRootRepository = new InMemoryWorkspaceRootRepository();
        workspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = workspaceRoot,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(workspaceRootRepository);
        var userContext = new TestUserContext();
        services.AddSingleton<IUserContext>(userContext);
        services.AddScoped(_ => new WorkspaceRootService(workspaceRootRepository, userContext));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new RepositorySessionSourceProvider(new RepositoryService(scopeFactory, NullLogger<RepositoryService>.Instance));
    }

    private sealed class GitRepositoryFixture : IDisposable
    {
        public GitRepositoryFixture()
        {
            ParentPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-root-{Guid.NewGuid():N}");
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
