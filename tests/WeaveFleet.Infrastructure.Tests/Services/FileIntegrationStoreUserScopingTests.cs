using System.Text.Json.Nodes;
using Shouldly;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// Verifies that <see cref="FileIntegrationStore"/> correctly scopes
/// data per-user and rejects malicious userId values.
/// </summary>
public sealed class FileIntegrationStoreUserScopingTests : IAsyncLifetime
{
    private const string UserAlice = "alice";
    private const string UserBob = "bob";
    private const string PluginId = "test-plugin";

    private readonly FileIntegrationStore _store = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up test data for both users
        await _store.RemoveConfigAsync(PluginId, UserAlice);
        await _store.RemoveConfigAsync(PluginId, UserBob);
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsNullForNonexistentUser()
    {
        var result = await _store.GetConfigAsync(PluginId, "nonexistent-user-xyz");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetAndGetConfigAsync_IsolatesDataBetweenUsers()
    {
        var aliceConfig = new JsonObject { ["key"] = "alice-value" };
        var bobConfig = new JsonObject { ["key"] = "bob-value" };

        await _store.SetConfigAsync(PluginId, UserAlice, aliceConfig);
        await _store.SetConfigAsync(PluginId, UserBob, bobConfig);

        var aliceResult = await _store.GetConfigAsync(PluginId, UserAlice);
        var bobResult = await _store.GetConfigAsync(PluginId, UserBob);

        aliceResult.ShouldNotBeNull();
        aliceResult["key"]!.GetValue<string>().ShouldBe("alice-value");

        bobResult.ShouldNotBeNull();
        bobResult["key"]!.GetValue<string>().ShouldBe("bob-value");
    }

    [Fact]
    public async Task RemoveConfigAsync_OnlyAffectsTargetUser()
    {
        var aliceConfig = new JsonObject { ["key"] = "alice-stays" };
        var bobConfig = new JsonObject { ["key"] = "bob-goes" };

        await _store.SetConfigAsync(PluginId, UserAlice, aliceConfig);
        await _store.SetConfigAsync(PluginId, UserBob, bobConfig);

        await _store.RemoveConfigAsync(PluginId, UserBob);

        var aliceResult = await _store.GetConfigAsync(PluginId, UserAlice);
        var bobResult = await _store.GetConfigAsync(PluginId, UserBob);

        aliceResult.ShouldNotBeNull();
        aliceResult["key"]!.GetValue<string>().ShouldBe("alice-stays");
        bobResult.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllConfigsAsync_ReturnsOnlyTargetUsersData()
    {
        var alicePlugin1 = new JsonObject { ["val"] = "a1" };
        var alicePlugin2 = new JsonObject { ["val"] = "a2" };
        var bobPlugin1 = new JsonObject { ["val"] = "b1" };

        await _store.SetConfigAsync("plugin1", UserAlice, alicePlugin1);
        await _store.SetConfigAsync("plugin2", UserAlice, alicePlugin2);
        await _store.SetConfigAsync("plugin1", UserBob, bobPlugin1);

        var aliceAll = await _store.GetAllConfigsAsync(UserAlice);

        aliceAll.ContainsKey("plugin1").ShouldBeTrue();
        aliceAll.ContainsKey("plugin2").ShouldBeTrue();
        aliceAll["plugin1"]["val"]!.GetValue<string>().ShouldBe("a1");
        aliceAll["plugin2"]["val"]!.GetValue<string>().ShouldBe("a2");

        var bobAll = await _store.GetAllConfigsAsync(UserBob);
        bobAll.ContainsKey("plugin1").ShouldBeTrue();
        bobAll["plugin1"]["val"]!.GetValue<string>().ShouldBe("b1");
        bobAll.ContainsKey("plugin2").ShouldBeFalse();

        // Cleanup extra plugins
        await _store.RemoveConfigAsync("plugin1", UserAlice);
        await _store.RemoveConfigAsync("plugin2", UserAlice);
        await _store.RemoveConfigAsync("plugin1", UserBob);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ValidateUserId_RejectsEmptyOrWhitespace(string userId)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _store.GetConfigAsync(PluginId, userId));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("user/sub")]
    [InlineData("user\\sub")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ValidateUserId_RejectsPathTraversalAttempts(string userId)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _store.GetConfigAsync(PluginId, userId));
    }
}
