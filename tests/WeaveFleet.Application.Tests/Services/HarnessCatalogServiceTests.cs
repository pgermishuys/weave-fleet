using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class HarnessCatalogServiceTests : IDisposable
{
    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _runtime = new("opencode");
    private readonly InMemoryHarnessProfileRepository _profiles = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("fleet-catalog-");
    private readonly HarnessCatalogService _sut;

    public HarnessCatalogServiceTests()
    {
        _registry.Register(_runtime);
        _registry.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsProfiles = true }));
        _sut = new HarnessCatalogService(
            _registry,
            new TestUserContext("user-1"),
            new FleetOptions(),
            NullLogger<HarnessCatalogService>.Instance,
            _profiles);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    private static readonly HarnessCatalog Catalog = new()
    {
        Agents = [new AgentInfo { Name = "loom", Mode = "primary" }],
        Providers = [],
        DefaultAgent = "loom",
    };

    [Fact]
    public async Task Asks_the_harness_for_the_folder_as_the_signed_in_user()
    {
        _runtime.CatalogBehavior = (_, _, _) => Task.FromResult<HarnessCatalog?>(Catalog);

        var result = await _sut.GetCatalogAsync("opencode", _folder.FullName + Path.DirectorySeparatorChar, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(Catalog);
        _runtime.CatalogCalls.ShouldHaveSingleItem().ShouldBe(("user-1", _folder.FullName, (string?)null));
    }

    private void SeedProfiles()
    {
        _profiles.Seed(new HarnessProfile { Id = "work", HarnessType = "opencode", Name = "Work", Content = "{}", IsDefault = true });
        _profiles.Seed(new HarnessProfile { Id = "local", HarnessType = "opencode", Name = "Local", Content = "{}" });
    }

    [Theory]
    [InlineData(null, "work")]
    [InlineData("local", "local")]
    [InlineData("none", null)]
    public async Task Asks_on_the_profile_a_new_session_would_use(string? asked, string? used)
    {
        SeedProfiles();

        var result = await _sut.GetCatalogAsync("opencode", _folder.FullName, CancellationToken.None, asked);

        result.IsSuccess.ShouldBeTrue();
        _runtime.CatalogCalls.ShouldHaveSingleItem().ProfileId.ShouldBe(used);
    }

    [Fact]
    public async Task An_unknown_profile_is_refused()
    {
        SeedProfiles();

        var result = await _sut.GetCatalogAsync("opencode", _folder.FullName, CancellationToken.None, "gone");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        _runtime.CatalogCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_harness_that_cant_list_one_gives_nothing()
    {
        var result = await _sut.GetCatalogAsync("opencode", _folder.FullName, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_harness_is_not_found()
    {
        var result = await _sut.GetCatalogAsync("pi", _folder.FullName, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("/no/such/fleet/folder")]
    public async Task A_folder_that_isnt_there_is_refused_without_asking_the_harness(string directory)
    {
        var result = await _sut.GetCatalogAsync("opencode", directory, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        _runtime.CatalogCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_harness_that_fails_gives_a_readable_error()
    {
        _runtime.CatalogBehavior = (_, _, _) => throw new HttpRequestException("connection refused");

        var result = await _sut.GetCatalogAsync("opencode", _folder.FullName, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Couldn't get the agents and models from the harness.");
    }
}
