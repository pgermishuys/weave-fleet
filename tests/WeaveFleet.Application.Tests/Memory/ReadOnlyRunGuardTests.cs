using Shouldly;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class ReadOnlyRunGuardTests
{
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ReadOnlyRunGuard _guard;

    public ReadOnlyRunGuardTests()
    {
        _sessions.Seed(
            new Session { Id = "dream", Tags = ["dreaming"], UserId = "owner" },
            new Session { Id = "dream-child", ParentSessionId = "dream", UserId = "owner" },
            new Session { Id = "work", UserId = "owner" });
        _guard = new ReadOnlyRunGuard([new Resolver()], new NoScope(), _sessions);
    }

    [Theory]
    [InlineData("hs-dream", "fleet_memory_save", true)]
    [InlineData("hs-dream", "fleet_message", true)]
    [InlineData("hs-dream", "fleet_session_start", true)]
    [InlineData("hs-dream", "fleet_browser_act", true)]
    [InlineData("hs-dream", "memory/save", true)]
    [InlineData("hs-dream-child", "fleet_page_show", true)]
    [InlineData("hs-dream", "fleet_session_read", false)]
    [InlineData("hs-work", "fleet_memory_save", false)]
    [InlineData("hs-unknown", "fleet_memory_save", false)]
    public async Task A_dreaming_session_and_its_subagents_are_refused_every_fleet_tool_but_fleet_session_read(string harnessSession, string tool, bool refused)
        => (await _guard.RefusesAsync("token", harnessSession, tool)).ShouldBe(refused);

    [Fact]
    public async Task A_call_with_no_token_or_session_is_left_to_the_tool_to_refuse()
    {
        (await _guard.RefusesAsync(null, "hs-dream", "fleet_memory_save")).ShouldBeFalse();
        (await _guard.RefusesAsync("token", null, "fleet_memory_save")).ShouldBeFalse();
    }

    private sealed class Resolver : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(harnessSessionId switch
            {
                "hs-dream" => new HarnessCanvasCaller("dream", "owner"),
                "hs-dream-child" => new HarnessCanvasCaller("dream-child", "owner"),
                "hs-work" => new HarnessCanvasCaller("work", "owner"),
                _ => null,
            });
    }

    private sealed class NoScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
