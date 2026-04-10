using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class DapperWorkspaceRootRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, WeaveFleet.Application.Data.IDbConnectionFactory Factory)> CreateAsync()
        => await TestDbHelper.CreateSharedDbAsync();

    [Fact]
    public async Task AddRootAsync_StampsOwnerAndOnlyOwnerCanListIt()
    {
        var (keeper, factory) = await CreateAsync();
        using var _ = keeper;
        using var tempDirectory = new TempDirectory();

        var ownerUserContext = new TestUserContext("owner-user");
        var ownerService = new WorkspaceRootService(
            new DapperWorkspaceRootRepository(factory, ownerUserContext),
            ownerUserContext);

        var result = await ownerService.AddRootAsync(tempDirectory.Path);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UserId.ShouldBe("owner-user");

        var ownerRoots = await new DapperWorkspaceRootRepository(factory, ownerUserContext).ListAsync();
        ownerRoots.Count.ShouldBe(1);
        ownerRoots[0].Id.ShouldBe(result.Value.Id);
        ownerRoots[0].Path.ShouldBe(result.Value.Path);
        ownerRoots[0].UserId.ShouldBe("owner-user");

        var otherRoots = await new DapperWorkspaceRootRepository(factory, new TestUserContext("other-user")).ListAsync();
        otherRoots.ShouldBeEmpty();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-root-{Guid.NewGuid():N}");
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
