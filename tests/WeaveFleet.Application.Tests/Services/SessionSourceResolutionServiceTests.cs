using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using Shouldly;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionSourceResolutionServiceTests
{
    private readonly InMemoryWorkspaceRootRepository _workspaceRootRepository = new();
    private readonly IUserContext _userContext = new TestUserContext();
    private readonly SessionSourceResolutionService _sut;

    public SessionSourceResolutionServiceTests()
    {
        _workspaceRootRepository.Seed(
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        );

        _sut = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(new WorkspaceRootService(_workspaceRootRepository, _userContext))
        ], new WeaveFleet.Application.Configuration.FleetOptions());
    }

    [Fact]
    public async Task ResolveCreateRequestAsync_InCloudModeWithoutDirectory_ReturnsManagedWorkspaceSelection()
    {
        var sut = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(new WorkspaceRootService(_workspaceRootRepository, _userContext)),
            new ManagedWorkspaceSessionSourceProvider(new WeaveFleet.Application.Configuration.FleetOptions
            {
                Cloud = new WeaveFleet.Application.Configuration.CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = Path.GetTempPath()
                }
            })
        ], new WeaveFleet.Application.Configuration.FleetOptions
        {
            Cloud = new WeaveFleet.Application.Configuration.CloudOptions
            {
                Enabled = true,
                WorkspaceRoot = Path.GetTempPath()
            }
        });

        var result = await sut.ResolveCreateRequestAsync(new CreateSessionRequest(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Descriptor.Key.ProviderId.ShouldBe(SessionSourceProviderIds.Managed);
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.IsolationStrategy.ShouldBe("managed");
    }

    private static bool CanCreateDirectorySymlink()
    {
        using var target = new TempDirectory();
        using var parent = new TempDirectory();
        var symlinkPath = Path.Combine(parent.Path, "symlink-check");

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
    public async Task ResolveCreateRequestAsync_TranslatesLegacyDirectoryPayload()
    {
        using var tempDirectory = new TempDirectory();

        var result = await _sut.ResolveCreateRequestAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            IsolationStrategy = "worktree",
            Branch = "feature/session-source"
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.Directory.ShouldBe(WorkspaceRootService.CanonicalizePath(tempDirectory.Path));
        result.Value.Input.WorkspaceIntent.IsolationStrategy.ShouldBe("worktree");
        result.Value.Input.WorkspaceIntent.Branch.ShouldBe("feature/session-source");
        result.Value.Input.Provenance.ProviderId.ShouldBe(SessionSourceProviderIds.Local);
        result.Value.Input.Provenance.ActionId.ShouldBe(SessionSourceActions.StartSession);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnknownProviderId()
    {
        using var tempDirectory = new TempDirectory();

        var result = await _sut.ResolveAsync(new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = "forged.provider",
                SourceType = SessionSourceTypeNames.Directory,
                ActionId = SessionSourceActions.StartSession,
                ContractVersion = 1
            },
            Input = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                directory = tempDirectory.Path
            })
        }, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionSource.ProviderId");
    }

    [Fact]
    public async Task ResolveForSessionActionAsync_RejectsMismatchedActionId()
    {
        using var tempDirectory = new TempDirectory();

        var result = await _sut.ResolveForSessionActionAsync(
            "session-1",
            new SessionSourceSelection
            {
                Key = SessionSourceCatalog.DirectoryStartSession.Key,
                Input = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    directory = tempDirectory.Path
                })
            },
            SessionSourceActions.AddToSession,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionSource.ActionId");
    }

    [Fact]
    public async Task ResolveCreateRequestAsync_RejectsDirectoryWhenNestedSymlinkEscapesAllowedRoots()
    {
        if (!CanCreateDirectorySymlink())
            return;

        using var allowedRoot = new TempDirectory();
        using var outsideParent = new TempDirectory();
        var nestedDirectory = Path.Combine(outsideParent.Path, "nested-dir");
        Directory.CreateDirectory(nestedDirectory);

        var symlinkDirectory = Path.Combine(allowedRoot.Path, "link-outside");
        Directory.CreateSymbolicLink(symlinkDirectory, outsideParent.Path);

        _workspaceRootRepository.Clear();
        _workspaceRootRepository.Seed(
            new WorkspaceRoot { Id = "root-1", Path = allowedRoot.Path, CreatedAt = DateTime.UtcNow.ToString("O") }
        );

        var result = await _sut.ResolveCreateRequestAsync(new CreateSessionRequest
        {
            Directory = Path.Combine(symlinkDirectory, "nested-dir"),
            IsolationStrategy = "existing"
        }, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside allowed workspace roots");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-session-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
