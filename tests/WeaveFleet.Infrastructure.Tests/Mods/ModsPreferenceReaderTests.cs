using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Users;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Tests.Data;

namespace WeaveFleet.Infrastructure.Tests.Mods;

public sealed class ModsPreferenceReaderTests : IAsyncLifetime
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
    public async Task Lists_the_mods_switch_and_own_bun_of_every_user_and_nothing_else()
    {
        await SetAsync("alice", "Mods", "true");
        await SetAsync("bob", "Mods", "false");
        await SetAsync("bob", "ModsBunPath", "/opt/tools/bun");
        await SetAsync("alice", "Theme", "dark");

        var rows = await new DapperModsPreferenceReader(_factory).ListAsync(CancellationToken.None);

        rows.OrderBy(r => r.UserId).ThenBy(r => r.Key).ToList().ShouldBe(
        [
            new ModsUserPreference("alice", "Mods", "true"),
            new ModsUserPreference("bob", "Mods", "false"),
            new ModsUserPreference("bob", "ModsBunPath", "/opt/tools/bun"),
        ]);
    }

    [Fact]
    public async Task Lists_nothing_when_nobody_has_chosen()
        => (await new DapperModsPreferenceReader(_factory).ListAsync(CancellationToken.None)).ShouldBeEmpty();

    private async Task SetAsync(string user, string key, string value)
    {
        await new DapperUserPreferenceRepository(_factory, new FixedUser(user)).SetAsync(key, value);
    }

    private sealed class FixedUser(string userId) : IUserContext
    {
        public string UserId { get; } = userId;
        public string? Email => null;
        public string? DisplayName => UserId;
        public bool IsAuthenticated => true;
    }
}
