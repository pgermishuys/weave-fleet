using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class ManagedWorkspaceTests
{
    [Fact]
    public async Task CreateWorkspaceAsync_InCloudMode_CreatesManagedWorkspaceUnderConfiguredRoot()
    {
        var workspaceRepository = new InMemoryWorkspaceRepository();
        var userContext = new TestUserContext("user_123|org:abc");

        using var workspaceRoot = new TempDirectory();
        var service = new WorkspaceService(
            workspaceRepository,
            userContext,
            new FleetOptions
            {
                Cloud = new CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = workspaceRoot.Path
                }
            },
            NullLogger<WorkspaceService>.Instance);

        var result = await service.CreateWorkspaceAsync("C:/ignored-source", "clone", "feature/cloud");

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsolationStrategy.ShouldBe("managed");
        result.Value.Branch.ShouldBe("feature/cloud");
        result.Value.UserId.ShouldBe("user_123|org:abc");
        Directory.Exists(result.Value.Directory).ShouldBeTrue();

        var segments = GetRelativeSegments(workspaceRoot.Path, result.Value.Directory);
        segments.Length.ShouldBe(2);
        segments[0].ShouldBe("user_123orgabc");
    }

    [Fact]
    public async Task CreateWorkspaceAsync_InCloudMode_UsesFallbackStorageKeyForEmptyOrHostileIdentity()
    {
        var workspaceRepository = new InMemoryWorkspaceRepository();

        using var workspaceRoot = new TempDirectory();
        var emptyIdentityService = new WorkspaceService(
            workspaceRepository,
            new TestUserContext(string.Empty),
            new FleetOptions
            {
                Cloud = new CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = workspaceRoot.Path
                }
            },
            NullLogger<WorkspaceService>.Instance);

        var hostileIdentityService = new WorkspaceService(
            workspaceRepository,
            new TestUserContext("!!!///...:::"),
            new FleetOptions
            {
                Cloud = new CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = workspaceRoot.Path
                }
            },
            NullLogger<WorkspaceService>.Instance);

        var emptyResult = await emptyIdentityService.CreateWorkspaceAsync("ignored", "existing");
        var hostileResult = await hostileIdentityService.CreateWorkspaceAsync("ignored", "existing");

        emptyResult.IsSuccess.ShouldBeTrue();
        hostileResult.IsSuccess.ShouldBeTrue();
        GetRelativeSegments(workspaceRoot.Path, emptyResult.Value.Directory)[0].ShouldBe("user");
        GetRelativeSegments(workspaceRoot.Path, hostileResult.Value.Directory)[0].ShouldBe("user");
    }

    [Fact]
    public async Task CreateWorkspaceAsync_InCloudMode_TruncatesSanitizedStorageKeyTo64Characters()
    {
        var workspaceRepository = new InMemoryWorkspaceRepository();
        var userId = new string('a', 80) + "|unsafe";

        using var workspaceRoot = new TempDirectory();
        var service = new WorkspaceService(
            workspaceRepository,
            new TestUserContext(userId),
            new FleetOptions
            {
                Cloud = new CloudOptions
                {
                    Enabled = true,
                    WorkspaceRoot = workspaceRoot.Path
                }
            },
            NullLogger<WorkspaceService>.Instance);

        var result = await service.CreateWorkspaceAsync("ignored", "existing");

        result.IsSuccess.ShouldBeTrue();
        var userSegment = GetRelativeSegments(workspaceRoot.Path, result.Value.Directory)[0];
        userSegment.Length.ShouldBe(64);
        userSegment.ShouldBe(new string('a', 64));
    }

    [Fact]
    public async Task CreateWorkspaceAsync_InLocalMode_LeavesDirectoryAndStrategyUnchanged()
    {
        var workspaceRepository = new InMemoryWorkspaceRepository();
        var service = new WorkspaceService(
            workspaceRepository,
            new TestUserContext("owner-1"),
            new FleetOptions(),
            NullLogger<WorkspaceService>.Instance);

        using var tempDirectory = new TempDirectory();

        var result = await service.CreateWorkspaceAsync(tempDirectory.Path, "existing");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Directory.ShouldBe(tempDirectory.Path);
        result.Value.IsolationStrategy.ShouldBe("existing");
    }

    private static string[] GetRelativeSegments(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath)
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-managed-workspace-{Guid.NewGuid():N}");
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
