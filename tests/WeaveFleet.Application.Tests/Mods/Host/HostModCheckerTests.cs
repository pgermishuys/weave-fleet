using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class HostModCheckerTests
{
    private sealed class OneCheckHost(Func<JsonElement> answer) : IModHost
    {
        public List<(string User, string Folder)> Asked { get; } = [];

        public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default)
        {
            Asked.Add((userId, folder));
            return Task.FromResult(answer());
        }

        public ModHostStatus GetStatus(string userId) => throw new NotSupportedException();
        public Task EnsureAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public ModLoadProblem? GetLoadProblem(string userId, string modId) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AsksTheCurrentUsersHostAndReturnsItsReport()
    {
        var report = JsonDocument.Parse("""{ "ok": true }""").RootElement.Clone();
        var host = new OneCheckHost(() => report);

        var result = await new HostModChecker(host, new TestUserContext("user-1")).CheckAsync("/staged/demo-mod");

        result!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        host.Asked.ShouldBe([("user-1", "/staged/demo-mod")]);
    }

    [Fact]
    public async Task AHostThatIsNotReadyBecomesAStoreRefusalWithTheReason()
    {
        var host = new OneCheckHost(() => throw new ModHostNotReadyException("Bun isn't installed."));

        var error = await Should.ThrowAsync<ModStoreException>(() => new HostModChecker(host, new TestUserContext("user-1")).CheckAsync("/staged/demo-mod"));

        error.Message.ShouldBe("The mod runtime isn't ready yet: Bun isn't installed.");
    }
}
