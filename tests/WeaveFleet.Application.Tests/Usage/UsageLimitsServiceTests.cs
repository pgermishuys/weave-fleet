using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Usage;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Usage;

/// <summary>When Fleet asks a provider, who may see the answer, and what clients are told.</summary>
public sealed class UsageLimitsServiceTests
{
    private const string User = "local-user";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly InMemoryUserPreferenceRepository _prefs = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly HarnessUsageLimits _live;
    private readonly FakeProvider _claude = new("claude", "Claude", needsConsent: false, "claude-code");
    private readonly FakeProvider _codex = new("codex", "Codex", needsConsent: true, "opencode");
    private readonly UsageLimitsService _service;

    public UsageLimitsServiceTests()
    {
        _live = new HarnessUsageLimits(_broadcaster, _time, NullLogger<HarnessUsageLimits>.Instance);
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IUserPreferenceRepository>(_prefs);
            services.AddSingleton<ISessionRepository>(_sessions);
        });
        _service = new UsageLimitsService([_claude, _codex], _live, _activity, _broadcaster, scopes, _time, NullLogger<UsageLimitsService>.Instance);
        _claude.Next = Ok("claude-account", 0.4);
        _codex.Next = Ok("codex-account", 0.6);
    }

    private static UsageReading Ok(string account, double used)
        => new(UsageStates.Ok, account, [new UsageLimitWindow { Window = "primary", Utilization = used, ResetsAt = Now.AddHours(1) }]);

    private static ProviderUsage Find(UsageLimitsSnapshot snapshot, string provider) => snapshot.Providers.Single(p => p.Provider == provider);

    [Fact]
    public async Task Without_consent_a_provider_is_listed_but_never_read_while_Claude_is()
    {
        var snapshot = await _service.GetAsync(User, watch: true, default);
        await _service.PollBusyAsync(User, default);

        snapshot.Enabled.ShouldBeTrue();
        Find(snapshot, "codex").State.ShouldBe(UsageStates.NotAllowed);
        _codex.Reads.ShouldBe(0);
        _claude.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task The_account_key_is_a_hash_of_the_account_id_alone()
    {
        _prefs.Seed("UsageAllow.codex", "true");
        _codex.AccountId = "acct-1";
        _claude.AccountId = null;

        var snapshot = await _service.GetAsync(User, watch: true, default);

        Find(snapshot, "codex").AccountKey.ShouldBe(AccountKey.For("codex", "acct-1"));
        Find(snapshot, "codex").AccountKey!.Length.ShouldBe(16);
        Find(snapshot, "codex").AccountKey!.ShouldNotContain("acct-1");
        Find(snapshot, "claude").AccountKey.ShouldBeNull();
        AccountKey.For("codex", "acct-2").ShouldNotBe(AccountKey.For("codex", "acct-1"));
        AccountKey.For("codex", "acct-1").ShouldNotBe(AccountKey.For("copilot", "acct-1"));
        AccountKey.For("codex", null).ShouldBeNull();
    }

    [Fact]
    public async Task A_reading_can_name_the_account_when_the_file_cannot()
    {
        _prefs.Seed("UsageAllow.codex", "true");
        _codex.Next = Ok("plus", 0.1) with { AccountId = "octocat" };

        var snapshot = await _service.GetAsync(User, watch: true, default);

        Find(snapshot, "codex").AccountKey.ShouldBe(AccountKey.For("codex", "octocat"));
    }

    [Fact]
    public async Task Without_the_popover_open_a_provider_is_asked_once_and_then_left_alone()
    {
        var first = await _service.GetAsync(User, watch: false, default);
        await _service.GetAsync(User, watch: false, default);

        _claude.Reads.ShouldBe(1);
        Find(first, "claude").State.ShouldBe(UsageStates.Ok);
        Find(first, "claude").ReadAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_provider_that_needs_Allow_is_not_asked_until_the_user_allows_it()
    {
        var snapshot = await _service.GetAsync(User, watch: true, default);

        Find(snapshot, "codex").State.ShouldBe(UsageStates.NotAllowed);
        Find(snapshot, "codex").Windows.ShouldBeEmpty();
        _codex.Reads.ShouldBe(0);
        _claude.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Allow_asks_at_once_and_revoking_hides_the_answer()
    {
        (await _service.SetAllowedAsync(User, "codex", true, default)).ShouldBeTrue();

        _codex.Reads.ShouldBe(1);
        (await _prefs.GetAsync("UsageAllow.codex")).ShouldBe("true");
        Find(await _service.GetAsync(User, false, default), "codex").Windows.Single().Utilization.ShouldBe(0.6);

        await _service.SetAllowedAsync(User, "codex", false, default);

        var after = Find(await _service.GetAsync(User, true, default), "codex");
        after.State.ShouldBe(UsageStates.NotAllowed);
        after.Windows.ShouldBeEmpty();
        _codex.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Allow_for_an_unknown_provider_is_refused_and_stores_nothing()
    {
        (await _service.SetAllowedAsync(User, "nope", true, default)).ShouldBeFalse();
        (await _prefs.GetAsync("UsageAllow.nope")).ShouldBeNull();
    }

    [Fact]
    public async Task An_answer_under_60_seconds_old_is_reused_and_an_older_one_is_asked_again()
    {
        await _service.GetAsync(User, watch: true, default);
        _time.Advance(TimeSpan.FromSeconds(59));
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(2));
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task The_last_answer_stays_with_its_read_time_when_nobody_is_watching()
    {
        await _service.GetAsync(User, watch: true, default);
        _time.Advance(TimeSpan.FromHours(3));

        var claude = Find(await _service.GetAsync(User, watch: false, default), "claude");

        claude.ReadAt.ShouldBe(Now);
        claude.Windows.Single().Utilization.ShouldBe(0.4);
        _claude.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_busy_session_makes_the_background_tick_ask_every_five_minutes()
    {
        _sessions.Seed(new Session { Id = "ses_1", HarnessType = "claude-code", UserId = User });
        _activity.Update("ses_1", "busy", User);

        _service.BusyUsers().ShouldBe([User]);
        await _service.PollBusyAsync(User, default);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _service.PollBusyAsync(User, default);
        _claude.Reads.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        await _service.PollBusyAsync(User, default);
        _claude.Reads.ShouldBe(2);
        _codex.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task An_idle_session_makes_nothing_happen()
    {
        _sessions.Seed(new Session { Id = "ses_1", HarnessType = "claude-code", UserId = User });
        _activity.Update("ses_1", "idle", User);

        _service.BusyUsers().ShouldBeEmpty();
        await _service.PollBusyAsync(User, default);
        _claude.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task A_429_backs_the_provider_off_and_the_wait_grows()
    {
        _claude.Next = new UsageReading(UsageStates.Unreadable) { BackOff = true };
        await _service.GetAsync(User, watch: true, default);

        _time.Advance(TimeSpan.FromMinutes(4));
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(2);

        // The second failure waits 10 minutes.
        _time.Advance(TimeSpan.FromMinutes(9));
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(2);
        _time.Advance(TimeSpan.FromMinutes(2));
        _claude.Next = Ok("claude-account", 0.5);
        await _service.GetAsync(User, watch: true, default);
        _claude.Reads.ShouldBe(3);
    }

    [Fact]
    public async Task A_provider_without_a_sign_in_is_left_out()
    {
        _codex.SignedIn = false;

        (await _service.GetAsync(User, watch: true, default)).Providers.Select(p => p.Provider).ShouldBe(["claude"]);
    }

    [Fact]
    public async Task Claudes_live_report_wins_over_a_polled_answer_only_when_it_is_newer()
    {
        await _service.GetAsync(User, watch: true, default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _live.ObserveAsync(User, "claude-code", new UsageLimitReport
        {
            Windows = [new UsageLimitWindow { Window = "primary", Utilization = 0.9, ResetsAt = Now.AddHours(1) }, new UsageLimitWindow { Window = "extra", Utilization = 0.1 }],
        });

        var merged = Find(await _service.GetAsync(User, watch: false, default), "claude");
        merged.Windows.Select(w => (w.Window, w.Utilization)).ShouldBe([("extra", 0.1), ("primary", 0.9)]);

        _time.Advance(TimeSpan.FromMinutes(2));
        _claude.Next = Ok("claude-account", 0.7);
        var polled = Find(await _service.GetAsync(User, watch: true, default), "claude");
        polled.Windows.Single().Utilization.ShouldBe(0.7);
    }

    [Fact]
    public async Task A_live_report_keeps_the_window_length_the_poll_found_and_the_payload_carries_it()
    {
        _claude.Next = new UsageReading(UsageStates.Ok, "max", [new UsageLimitWindow { Window = "five_hour", Utilization = 0.4, WindowSeconds = 18000 }]);
        await _service.GetAsync(User, watch: true, default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _live.ObserveAsync(User, "claude-code", new UsageLimitReport { Windows = [new UsageLimitWindow { Window = "five_hour", Utilization = 0.5 }] });

        var merged = Find(await _service.GetAsync(User, watch: false, default), "claude").Windows.Single();

        (merged.Utilization, merged.WindowSeconds).ShouldBe((0.5, 18000));
        var first = _broadcaster.Broadcasts[0].Payload.GetProperty("providers")[0].GetProperty("windows")[0];
        first.GetProperty("windowSeconds").GetInt32().ShouldBe(18000);
    }

    [Theory]
    [InlineData("pi")]
    [InlineData("copilot")]
    public async Task A_busy_Pi_or_Copilot_session_makes_the_copilot_provider_poll(string harness)
    {
        var copilot = new FakeProvider("copilot", "GitHub Copilot", needsConsent: true, "copilot", "pi") { Next = Ok("pro", 0.2) };
        _prefs.Seed(UsageLimitsService.AllowPrefix + "copilot", "true");
        var service = new UsageLimitsService([copilot], _live, _activity, _broadcaster,
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IUserPreferenceRepository>(_prefs);
                services.AddSingleton<ISessionRepository>(_sessions);
            }), _time, NullLogger<UsageLimitsService>.Instance);
        _sessions.Seed(new Session { Id = "ses_1", HarnessType = harness, UserId = User });
        _activity.Update("ses_1", "busy", User);

        await service.PollBusyAsync(User, default);

        copilot.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Clients_are_told_once_per_change_and_never_see_a_token()
    {
        _claude.Next = new UsageReading(UsageStates.Ok, "max", [new UsageLimitWindow { Window = "five_hour", Utilization = 0.4 }]);
        await _service.GetAsync(User, watch: true, default);
        _broadcaster.Broadcasts.Count.ShouldBe(1);
        var sent = _broadcaster.Broadcasts[0];
        sent.Topic.ShouldBe("sessions");
        sent.Type.ShouldBe("usage.limits");
        sent.UserId.ShouldBe(User);
        sent.Payload.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        var claude = sent.Payload.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "claude");
        claude.GetProperty("source").GetString().ShouldBe("Claude Code");
        claude.GetProperty("account").GetString().ShouldBe("max");
        claude.GetProperty("credits").ValueKind.ShouldBe(JsonValueKind.Null);
        claude.GetProperty("harnesses")[0].GetString().ShouldBe("claude-code");
        sent.Payload.GetRawText().ShouldNotContain("secret");

        // Nothing changed: the answer is the same, so no second event.
        await _service.SetAllowedAsync(User, "claude", true, default);
        _broadcaster.Broadcasts.Count.ShouldBe(1);
    }

    [Fact]
    public void A_snapshot_serializes_in_the_contract_s_shape()
    {
        var snapshot = new UsageLimitsSnapshot(true,
        [
            new ProviderUsage("openrouter", "OpenRouter", UsageStates.Ok, ["opencode"], [], null, "OpenCode", Now, new UsageCredits(5, 10, 5)),
        ]);

        var json = JsonSerializer.SerializeToElement(snapshot, ApplicationJsonContext.Default.UsageLimitsSnapshot).GetProperty("providers")[0];

        json.GetProperty("account").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("credits").GetProperty("remaining").GetDouble().ShouldBe(5);
        json.GetProperty("readAt").GetDateTimeOffset().ShouldBe(Now);
        json.GetProperty("windows").GetArrayLength().ShouldBe(0);
    }

    private sealed class FakeProvider(string provider, string name, bool needsConsent, params string[] harnesses) : IUsageProvider
    {
        public string Provider => provider;
        public string Name => name;
        public string Source => provider == "claude" ? "Claude Code" : "OpenCode";
        public IReadOnlyList<string> Harnesses { get; } = harnesses;
        public bool NeedsConsent => needsConsent;
        public string? AccountId { get; set; }
        public bool SignedIn { get; set; } = true;
        public UsageReading Next { get; set; } = new(UsageStates.Ok);
        public int Signins { get; private set; }
        public int Reads { get; private set; }

        public Task<bool> HasSignInAsync(CancellationToken ct)
        {
            Signins++;
            return Task.FromResult(SignedIn);
        }

        public Task<UsageReading> ReadAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Next);
        }
    }
}
