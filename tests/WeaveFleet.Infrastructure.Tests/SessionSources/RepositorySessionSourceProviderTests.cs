using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.SessionSources;
using WeaveFleet.Testing.Fakes.Repositories;

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
        using var repository = new RealGitRepositoryFixture();
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

    /// <summary>Creates a real git repo (with git init) so that worktree operations work.</summary>
    private sealed class RealGitRepositoryFixture : IDisposable
    {
        public RealGitRepositoryFixture()
        {
            ParentPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-root-{Guid.NewGuid():N}");
            Path = System.IO.Path.Combine(ParentPath, "repo");
            Directory.CreateDirectory(Path);

            RunGit(Path, "init");
            RunGit(Path, "config", "user.email", "test@test.com");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "commit", "--allow-empty", "-m", "initial");
        }

        public string ParentPath { get; }
        public string Path { get; }
        private readonly List<string> _worktreePaths = [];

        public string CreateWorktree(string branchName)
        {
            var worktreeDir = System.IO.Path.Combine(ParentPath, $"repo-worktrees", branchName);
            RunGit(Path, "worktree", "add", worktreeDir, "-b", branchName);
            _worktreePaths.Add(worktreeDir);
            return worktreeDir;
        }

        public void Dispose()
        {
            // Remove worktrees first to release git locks
            foreach (var wt in _worktreePaths)
            {
                try { RunGit(Path, "worktree", "remove", "--force", wt); } catch { }
            }
            try { RunGit(Path, "worktree", "prune"); } catch { }

            if (Directory.Exists(ParentPath))
            {
                // Git objects on Windows are often read-only; clear attributes before deletion
                foreach (var file in Directory.EnumerateFiles(ParentPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        Directory.Delete(ParentPath, recursive: true);
                        return;
                    }
                    catch (UnauthorizedAccessException) when (i < 2)
                    {
                        Thread.Sleep(100);
                    }
                    catch (IOException) when (i < 2)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private static void RunGit(string workDir, params string[] args)
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                proc.StartInfo.ArgumentList.Add(arg);
            proc.Start();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
            }
        }
    }
}
