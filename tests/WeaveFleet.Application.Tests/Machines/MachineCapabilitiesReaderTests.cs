using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

public sealed class MachineCapabilitiesReaderTests
{
    private readonly CountingRegistry _registry = new();
    private readonly HarnessAvailabilityCache _cache;
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FleetOptions _options = new();
    private readonly MachineCapabilitiesReader _reader;

    public MachineCapabilitiesReaderTests()
    {
        _cache = new HarnessAvailabilityCache(_registry, TimeProvider.System, NullLogger<HarnessAvailabilityCache>.Instance);
        _reader = new MachineCapabilitiesReader(_options, _cache, _preferences, _sessions, _activity);
    }

    [Fact]
    public async Task It_takes_peer_messages_only_with_a_machine_token()
    {
        (await _reader.ReadAsync()).PeerMessages.ShouldBeTrue();

        _options.Auth.Enabled = true;
        (await _reader.ReadAsync()).PeerMessages.ShouldBeFalse();

        _options.Auth.Enabled = false;
        _options.Auth.TokenAuthEnabled = false;
        (await _reader.ReadAsync()).PeerMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task Harnesses_are_null_until_the_first_check_and_reading_never_starts_one()
    {
        var capabilities = await _reader.ReadAsync();

        capabilities.Harnesses.ShouldBeNull();
        _registry.Checks.ShouldBe(0);
    }

    [Fact]
    public async Task Harnesses_come_from_the_kept_check_and_are_on_until_the_user_turns_them_off()
    {
        await _cache.GetAsync(fresh: false, CancellationToken.None);
        _preferences.Seed("claude-code.enabled", "false");

        var harnesses = (await _reader.ReadAsync()).Harnesses!;

        harnesses.ShouldBe(
        [
            new MachineHarness("opencode", "OpenCode", Available: true, Enabled: true, "1.18.32"),
            new MachineHarness("claude-code", "Claude Code", Available: true, Enabled: false, "2.1.0"),
            new MachineHarness("pi", "Pi", Available: false, Enabled: true, null),
        ]);
        _registry.Checks.ShouldBe(1);
    }

    [Fact]
    public async Task Sessions_count_top_level_work_and_questions_as_the_list_shows_them()
    {
        Seed("working", "busy");
        Seed("retrying", "retry");
        Seed("asking", "waiting_input");
        Seed("idle", "idle");
        Seed("untracked", status: null);
        Seed("finished", "busy", sessionStatus: "completed");
        Seed("subagent", "busy", parentSessionId: "working");
        Seed("side-chat", "busy", sideOf: "working");
        // A parent whose delegated child stops on a question needs the user, not the agent.
        Seed("parent", "busy");
        Seed("child", "waiting_input", parentSessionId: "parent");
        _activity.RegisterChild("child", "parent");

        var sessions = (await _reader.ReadAsync()).Sessions;

        sessions.ShouldBe(new MachineSessionCounts(Working: 2, NeedsYou: 2));
    }

    private void Seed(string id, string? status, string sessionStatus = "active", string? parentSessionId = null, string? sideOf = null)
    {
        _sessions.Seed(new Session { Id = id, Status = sessionStatus, ParentSessionId = parentSessionId, SideOfSessionId = sideOf });
        if (status is not null)
            _activity.Update(id, status, "local-user");
    }

    private sealed class CountingRegistry : IHarnessRegistry
    {
        private int _checks;

        public int Checks => Volatile.Read(ref _checks);

        public IReadOnlyList<IHarness> GetAll() => [];
        public IHarness? GetByType(string harnessType) => null;
        public IHarnessRuntime? GetRuntimeByType(string harnessType) => null;

        public Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _checks);
            return Task.FromResult<IReadOnlyList<HarnessInfo>>(
            [
                HarnessInfo.From("opencode", "OpenCode", new HarnessCapabilities(), HarnessAvailability.Ready("1.18.32", "/usr/bin/opencode")),
                HarnessInfo.From("claude-code", "Claude Code", new HarnessCapabilities(), HarnessAvailability.Ready("2.1.0", "/usr/bin/claude")),
                HarnessInfo.From("pi", "Pi", new HarnessCapabilities(), HarnessAvailability.NotInstalled("Pi isn't installed.")),
            ]);
        }
    }
}
