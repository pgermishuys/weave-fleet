using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Runtimes;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunPathSettingTests : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private IDbConnectionFactory _factory = null!;

    public async Task InitializeAsync() => (_keeper, _factory) = await TestDbHelper.CreateSharedDbAsync();

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Saves_a_path_for_one_user_and_reads_it_back()
    {
        var setting = NewSetting();

        await setting.SaveAsync("alice", "/opt/tools/bun", CancellationToken.None);

        (await setting.GetAsync("alice", CancellationToken.None)).ShouldBe("/opt/tools/bun");
        setting.FromConfiguration.ShouldBeNull();
    }

    [Fact]
    public async Task One_users_path_is_not_another_users()
    {
        var setting = NewSetting();
        await setting.SaveAsync("alice", "/opt/alice/bun", CancellationToken.None);
        await setting.SaveAsync("bob", "/opt/bob/bun", CancellationToken.None);

        (await setting.GetAsync("alice", CancellationToken.None)).ShouldBe("/opt/alice/bun");
        (await setting.GetAsync("bob", CancellationToken.None)).ShouldBe("/opt/bob/bun");
        (await setting.GetAsync("carol", CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clearing_a_path_leaves_none(string? cleared)
    {
        var setting = NewSetting();
        await setting.SaveAsync("alice", "/opt/alice/bun", CancellationToken.None);

        await setting.SaveAsync("alice", cleared, CancellationToken.None);

        (await setting.GetAsync("alice", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Stores_the_path_under_the_ModsBunPath_preference()
    {
        await NewSetting().SaveAsync("alice", "/opt/alice/bun", CancellationToken.None);

        using var scope = Provider().CreateScope();
        using (new BackgroundUserScope().Begin("alice"))
            (await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync("ModsBunPath")).ShouldBe("/opt/alice/bun");
        BunPathPreference.Key.ShouldBe("ModsBunPath");
    }

    [Fact]
    public async Task The_configured_path_wins_for_every_user()
    {
        var setting = NewSetting("/etc/fleet/bun");
        setting.FromConfiguration.ShouldBe("/etc/fleet/bun");

        (await setting.GetAsync("alice", CancellationToken.None)).ShouldBe("/etc/fleet/bun");
        (await setting.GetAsync("bob", CancellationToken.None)).ShouldBe("/etc/fleet/bun");
    }

    [Fact]
    public async Task A_saved_path_is_ignored_while_configuration_sets_one()
    {
        await NewSetting().SaveAsync("alice", "/opt/alice/bun", CancellationToken.None);

        (await NewSetting("/etc/fleet/bun").GetAsync("alice", CancellationToken.None)).ShouldBe("/etc/fleet/bun");
    }

    [Fact]
    public async Task Cannot_save_while_configuration_sets_a_path()
    {
        var setting = NewSetting("/etc/fleet/bun");

        await Should.ThrowAsync<InvalidOperationException>(() => setting.SaveAsync("alice", "/opt/alice/bun", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_path_is_none(string configured)
        => NewSetting(configured).FromConfiguration.ShouldBeNull();

    private BunPathSetting NewSetting(string configured = "")
        => new(
            new FleetOptions { Harness = { BunPath = configured } },
            Provider().GetRequiredService<IServiceScopeFactory>(),
            new BackgroundUserScope());

    private ServiceProvider? _provider;

    private ServiceProvider Provider()
    {
        if (_provider is not null)
            return _provider;

        var services = new ServiceCollection();
        services.AddSingleton(_factory);
        services.AddScoped<IUserContext, LocalUserContext>();
        services.AddScoped<IUserPreferenceRepository, DapperUserPreferenceRepository>();
        return _provider = services.BuildServiceProvider();
    }
}
