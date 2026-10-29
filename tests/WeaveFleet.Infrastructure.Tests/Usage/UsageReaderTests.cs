using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Usage;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Usage;

namespace WeaveFleet.Infrastructure.Tests.Usage;

/// <summary>The readers against a fake provider API and sign-in files in a temp folder. Every shape is invented but follows what the providers send.</summary>
public sealed class UsageReaderTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("fleet-usage-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeApi _api = new();
    private readonly Dictionary<string, string?> _env = [];

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private UsageSources Sources() => new(_home, name => _env.GetValueOrDefault(name));
    private ClaudeUsageProvider Claude() => new(_api, Sources(), _time);
    private CodexUsageProvider Codex() => new(_api, Sources(), _time);
    private OpenRouterUsageProvider OpenRouter() => new(_api, Sources(), _time);

    private bool _copilotReady = true;
    private string? _githubToken;
    private Func<CancellationToken, Task<JsonElement>> _askCopilot = _ => throw new InvalidOperationException("not asked");
    private CopilotUsageProvider Copilot() => new(_api, Sources(), _ => Task.FromResult(_copilotReady), ct => _askCopilot(ct), _ => Task.FromResult(_githubToken));
    private void CopilotReplies(string json) => _askCopilot = _ => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void ClaudeSignIn(long? expiresAt = null)
    {
        var expires = expiresAt is null ? "" : ",\"expiresAt\":" + expiresAt;
        Write(".claude/.credentials.json", "{\"claudeAiOauth\":{\"accessToken\":\"claude-secret\",\"subscriptionType\":\"max\"" + expires + "}}");
    }

    private void OpenCodeAuth(string json) => Write(".local/share/opencode/auth.json", json);

    private const string ClaudeBody = """
        {"five_hour":{"utilization":42.5,"resets_at":"2026-10-20T15:00:00+00:00"},
         "seven_day":{"utilization":85,"resets_at":"2026-10-25T00:00:00Z"},
         "seven_day_opus":null,
         "seven_day_sonnet":{"utilization":100,"resets_at":"2026-10-25T00:00:00Z"}}
        """;

    [Fact]
    public async Task claude_a_seat_with_a_spend_cap_and_no_windows_has_no_limits_to_show_not_an_error()
    {
        ClaudeSignIn();
        _api.Reply = (HttpStatusCode.OK, """
            {"five_hour":null,"seven_day":null,"seven_day_opus":null,"seven_day_sonnet":null,
             "extra_usage":{"is_enabled":true,"monthly_limit":5000,"used_credits":1200.5,"utilization":24.01}}
            """);

        var reading = await Claude().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.NoWindow);
        reading.Account.ShouldBe("max");
    }

    [Fact]
    public async Task claude_reads_percentages_as_fractions_and_sends_the_token_and_beta_header()
    {
        ClaudeSignIn();
        _api.Reply = (HttpStatusCode.OK, ClaudeBody);

        var reading = await Claude().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Account.ShouldBe("max");
        reading.Windows!.Select(w => (w.Window, w.Utilization, w.Status)).ShouldBe(
        [
            ("five_hour", 0.425, "allowed"), ("seven_day", 0.85, "warning"), ("seven_day_sonnet", 1.0, "rejected"),
        ]);
        reading.Windows![0].ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero));
        reading.Windows!.Select(w => w.WindowSeconds).ShouldBe([18000, 604800, 604800]);
        _api.Requests.Single().Uri.ShouldBe("https://api.anthropic.com/api/oauth/usage");
        _api.Requests.Single().Authorization.ShouldBe("Bearer claude-secret");
        _api.Requests.Single().Headers["anthropic-beta"].ShouldBe("oauth-2025-04-20");
    }

    [Fact]
    public async Task claude_base_address_follows_the_environment_variable()
    {
        ClaudeSignIn();
        _env["FLEET_USAGE_ANTHROPIC_URL"] = "http://127.0.0.1:9000/";
        _api.Reply = (HttpStatusCode.OK, ClaudeBody);

        await Claude().ReadAsync(default);

        _api.Requests.Single().Uri.ShouldBe("http://127.0.0.1:9000/api/oauth/usage");
    }

    [Fact]
    public async Task claude_with_an_expired_token_is_signed_out_without_asking()
    {
        ClaudeSignIn(_time.GetUtcNow().AddMinutes(-1).ToUnixTimeMilliseconds());

        (await Claude().ReadAsync(default)).State.ShouldBe(UsageStates.SignedOut);
        _api.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageStates.SignedOut, true)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageStates.Unreadable, true)]
    [InlineData(HttpStatusCode.InternalServerError, UsageStates.Unreadable, false)]
    public async Task claude_failures_are_states(HttpStatusCode status, string state, bool backOff)
    {
        ClaudeSignIn();
        _api.Reply = (status, "{}");

        var reading = await Claude().ReadAsync(default);

        reading.State.ShouldBe(state);
        reading.BackOff.ShouldBe(backOff);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"five_hour":{"utilization":"lots"}}""")]
    [InlineData("""{"something_new":{}}""")]
    public async Task claude_an_answer_it_does_not_know_is_unreadable(string body)
    {
        ClaudeSignIn();
        _api.Reply = (HttpStatusCode.OK, body);

        (await Claude().ReadAsync(default)).State.ShouldBe(UsageStates.Unreadable);
    }

    [Fact]
    public async Task claude_without_a_sign_in_file_has_no_sign_in()
    {
        (await Claude().HasSignInAsync(default)).ShouldBeFalse();
        Write(".claude/.credentials.json", "{ broken");
        (await Claude().HasSignInAsync(default)).ShouldBeFalse();
        ClaudeSignIn();
        (await Claude().HasSignInAsync(default)).ShouldBeTrue();
    }

    [Fact]
    public async Task codex_reads_both_windows_with_the_account_header()
    {
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"codex-secret","refresh":"r","accountId":"acct-9"}}""");
        _api.Reply = (HttpStatusCode.OK, """
            {"plan_type":"plus","rate_limit":{
              "primary_window":{"used_percent":30,"limit_window_seconds":18000,"reset_at":1792500000},
              "secondary_window":{"used_percent":90,"limit_window_seconds":604800,"reset_after_seconds":3600}}}
            """);

        var reading = await Codex().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Account.ShouldBe("plus");
        reading.Windows![0].Window.ShouldBe("primary");
        reading.Windows![0].Utilization.ShouldBe(0.3);
        reading.Windows![0].ResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1792500000));
        reading.Windows![1].Window.ShouldBe("secondary");
        reading.Windows![1].Status.ShouldBe("warning");
        reading.Windows![1].ResetsAt.ShouldBe(_time.GetUtcNow().AddHours(1));
        reading.Windows!.Select(w => w.WindowSeconds).ShouldBe([18000, 604800]);
        _api.Requests.Single().Uri.ShouldBe("https://chatgpt.com/backend-api/wham/usage");
        _api.Requests.Single().Authorization.ShouldBe("Bearer codex-secret");
        _api.Requests.Single().Headers["ChatGPT-Account-Id"].ShouldBe("acct-9");
    }

    [Fact]
    public async Task codex_honors_xdg_data_home()
    {
        var xdg = Path.Combine(_home, "elsewhere");
        _env["XDG_DATA_HOME"] = xdg;
        Directory.CreateDirectory(Path.Combine(xdg, "opencode"));
        File.WriteAllText(Path.Combine(xdg, "opencode", "auth.json"), """{"openai":{"type":"oauth","access":"t"}}""");

        (await Codex().HasSignInAsync(default)).ShouldBeTrue();
    }

    [Fact]
    public async Task codex_on_an_api_key_has_no_window_and_asks_nothing()
    {
        OpenCodeAuth("""{"openai":{"type":"api","key":"sk-invented"}}""");

        (await Codex().HasSignInAsync(default)).ShouldBeTrue();
        (await Codex().ReadAsync(default)).State.ShouldBe(UsageStates.NoWindow);
        _api.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageStates.SignedOut)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageStates.Unreadable)]
    public async Task codex_failures_are_states(HttpStatusCode status, string state)
    {
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"t"}}""");
        _api.Reply = (status, "");

        (await Codex().ReadAsync(default)).State.ShouldBe(state);
    }

    [Fact]
    public async Task codex_an_answer_without_windows_is_unreadable()
    {
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"t"}}""");
        _api.Reply = (HttpStatusCode.OK, """{"plan_type":"plus","rate_limit":{}}""");

        (await Codex().ReadAsync(default)).State.ShouldBe(UsageStates.Unreadable);
    }

    [Fact]
    public async Task codex_without_a_file_has_no_sign_in()
        => (await Codex().HasSignInAsync(default)).ShouldBeFalse();

    [Fact]
    public async Task openrouter_reads_credits_and_no_windows()
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"or-secret"}}""");
        _api.Reply = (HttpStatusCode.OK, """{"data":{"label":"k","limit":20,"limit_remaining":12.5,"usage":7.5}}""");

        var reading = await OpenRouter().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Windows.ShouldBeNull();
        reading.Credits.ShouldBe(new UsageCredits(12.5, 20, 7.5));
        _api.Requests.Single().Uri.ShouldBe("https://openrouter.ai/api/v1/key");
        _api.Requests.Single().Authorization.ShouldBe("Bearer or-secret");
    }

    [Fact]
    public async Task openrouter_a_key_without_a_limit_reads_usage_only()
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"k"}}""");
        _api.Reply = (HttpStatusCode.OK, """{"data":{"limit":null,"limit_remaining":null,"usage":3}}""");

        (await OpenRouter().ReadAsync(default)).Credits.ShouldBe(new UsageCredits(null, null, 3));
    }

    [Theory]
    [InlineData("""{"oops":1}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("[]")]
    public async Task openrouter_an_unknown_shape_is_unreadable(string body)
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"k"}}""");
        _api.Reply = (HttpStatusCode.OK, body);

        (await OpenRouter().ReadAsync(default)).State.ShouldBe(UsageStates.Unreadable);
    }

    [Fact]
    public async Task openrouter_429_backs_off()
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"k"}}""");
        _api.Reply = (HttpStatusCode.TooManyRequests, "");

        (await OpenRouter().ReadAsync(default)).BackOff.ShouldBeTrue();
    }

    [Fact]
    public async Task a_network_error_is_unreadable()
    {
        ClaudeSignIn();
        _api.Throw = true;

        (await Claude().ReadAsync(default)).State.ShouldBe(UsageStates.Unreadable);
    }

    private const string CopilotBody = """
        {"authenticated":true,"login":"octo-invented","plan":"pro","resetAt":"2026-11-01T00:00:00Z","quotas":[
          {"id":"premium_interactions","entitlement":300,"remaining":75,"percentRemaining":25,"unlimited":false},
          {"id":"chat","entitlement":null,"remaining":null,"percentRemaining":null,"unlimited":true},
          {"id":"completions","entitlement":null,"remaining":null,"percentRemaining":50,"unlimited":false}]}
        """;

    [Fact]
    public async Task copilot_reads_one_window_per_limited_quota_and_skips_unlimited()
    {
        CopilotReplies(CopilotBody);

        var reading = await Copilot().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Account.ShouldBe("pro");
        reading.Windows!.Select(w => (w.Window, w.Utilization, w.Status)).ShouldBe([("premium_interactions", 0.75, "allowed"), ("completions", 0.5, "allowed")]);
        reading.Windows![0].ResetsAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        // October has 31 days.
        reading.Windows![0].WindowSeconds.ShouldBe(31 * 86400);
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task copilot_not_authenticated_is_signed_out()
    {
        CopilotReplies("""{"authenticated":false,"login":null,"plan":null,"resetAt":null,"quotas":[]}""");

        (await Copilot().ReadAsync(default)).State.ShouldBe(UsageStates.SignedOut);
    }

    [Fact]
    public async Task copilot_method_not_found_in_an_older_weave_copilot_is_unreadable_and_quiet()
    {
        _askCopilot = _ => throw new AcpException("Method not found", -32601);

        var reading = await Copilot().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Unreadable);
        reading.BackOff.ShouldBeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"authenticated":true,"quotas":[]}""")]
    public async Task copilot_an_answer_without_quotas_is_unreadable(string body)
    {
        CopilotReplies(body);

        (await Copilot().ReadAsync(default)).State.ShouldBe(UsageStates.Unreadable);
    }

    [Fact]
    public async Task copilot_with_every_quota_unlimited_has_no_limit_to_show()
    {
        // An enterprise seat on token billing (29 October 2026): every quota unlimited, entitlement 0.
        CopilotReplies("""
            {"authenticated":true,"login":"octo-invented","plan":"enterprise","resetAt":"2026-11-01T00:00:00Z","quotas":[
              {"id":"premium_interactions","entitlement":0,"remaining":0,"percentRemaining":100,"unlimited":true},
              {"id":"chat","entitlement":0,"remaining":0,"percentRemaining":100,"unlimited":true}]}
            """);

        var reading = await Copilot().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.NoWindow);
        reading.Account.ShouldBe("enterprise");
    }

    [Fact]
    public async Task copilot_has_a_sign_in_when_the_harness_is_ready_or_pi_has_a_github_token()
    {
        _copilotReady = false;
        (await Copilot().HasSignInAsync(default)).ShouldBeFalse();
        _githubToken = "gh-secret";
        (await Copilot().HasSignInAsync(default)).ShouldBeTrue();
        _githubToken = null;
        _copilotReady = true;
        (await Copilot().HasSignInAsync(default)).ShouldBeTrue();
    }

    [Fact]
    public async Task copilot_for_pi_asks_github_directly_with_the_token()
    {
        _copilotReady = false;
        _githubToken = "gh-secret";
        _api.Reply = (HttpStatusCode.OK, """
            {"login":"octo-invented","copilot_plan":"individual","quota_reset_date":"2026-12-01","quota_snapshots":{
              "premium_interactions":{"quota_id":"premium_interactions","entitlement":300,"remaining":0,"percent_remaining":0,"unlimited":false},
              "chat":{"quota_id":"chat","unlimited":true,"entitlement":0,"remaining":0}}}
            """);

        var reading = await Copilot().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Account.ShouldBe("individual");
        var window = reading.Windows!.Single();
        (window.Window, window.Utilization, window.Status).ShouldBe(("premium_interactions", 1.0, "rejected"));
        window.WindowSeconds.ShouldBe(30 * 86400);
        _api.Requests.Single().Uri.ShouldBe("https://api.github.com/copilot_internal/user");
        _api.Requests.Single().Authorization.ShouldBe("Bearer gh-secret");
    }

    [Fact]
    public async Task copilot_github_base_address_follows_the_environment_variable()
    {
        _copilotReady = false;
        _githubToken = "t";
        _env["FLEET_USAGE_GITHUB_URL"] = "http://127.0.0.1:9100/";
        _api.Reply = (HttpStatusCode.OK, "{}");

        await Copilot().ReadAsync(default);

        _api.Requests.Single().Uri.ShouldBe("http://127.0.0.1:9100/copilot_internal/user");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageStates.SignedOut)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageStates.Unreadable)]
    [InlineData(HttpStatusCode.OK, UsageStates.Unreadable)]
    public async Task copilot_for_pi_failures_are_states_and_the_token_is_not_in_them(HttpStatusCode status, string state)
    {
        _copilotReady = false;
        _githubToken = "gh-secret";
        _api.Reply = (status, "{}");

        var reading = await Copilot().ReadAsync(default);

        reading.State.ShouldBe(state);
        reading.Account.ShouldBeNull();
    }

    [Fact]
    public async Task copilot_prefers_the_harness_when_both_exist()
    {
        CopilotReplies(CopilotBody);
        _githubToken = "gh-secret";

        (await Copilot().ReadAsync(default)).State.ShouldBe(UsageStates.Ok);
        _api.Requests.ShouldBeEmpty();
    }

    /// <summary>A fake provider API: one canned reply, and a record of what was asked.</summary>

    // ---- OpenCode 2: sign-ins in its credential table ----

    private string Oc2Db => Path.Combine(_home, ".weave", "harnesses", "opencode2", "data", "opencode.db");

    private void Oc2Credential(string integration, string value, bool active = true, string? db = null)
    {
        db ??= Oc2Db;
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS credential (id TEXT PRIMARY KEY, integration_id TEXT, label TEXT NOT NULL, value TEXT NOT NULL,
              connector_id TEXT, method_id TEXT, active INTEGER, time_created INTEGER, time_updated INTEGER);
            INSERT INTO credential (id, integration_id, label, value, active) VALUES ($id, $i, 'x', $v, $a);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$i", integration);
        command.Parameters.AddWithValue("$v", value);
        command.Parameters.AddWithValue("$a", active ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static string Oc2OAuth(long expiresMs) => "{\"type\":\"oauth\",\"methodID\":\"chatgpt\",\"refresh\":\"oc2-refresh\",\"access\":\"oc2-secret\",\"expires\":" + expiresMs + ",\"metadata\":{\"accountID\":\"acct-oc2\"}}";
    private long InAnHour => _time.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds();

    private const string CodexBody = """{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000,"reset_after_seconds":60}}}""";

    [Fact]
    public async Task codex_reads_the_opencode2_sign_in_from_its_database()
    {
        Oc2Credential("openai", Oc2OAuth(InAnHour));
        _api.Reply = (HttpStatusCode.OK, CodexBody);

        var codex = Codex();
        (await codex.HasSignInAsync(default)).ShouldBeTrue();
        codex.Harnesses.ShouldBe(["opencode2"]);
        codex.Source.ShouldBe("OpenCode 2");
        var reading = await codex.ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        _api.Requests.Single().Authorization.ShouldBe("Bearer oc2-secret");
        _api.Requests.Single().Headers["ChatGPT-Account-Id"].ShouldBe("acct-oc2");
        JsonSerializer.Serialize(reading).ShouldNotContain("oc2-secret");
    }

    [Fact]
    public async Task codex_with_both_opencodes_lists_both_and_prefers_the_unexpired_token()
    {
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"oc1-secret"}}""");
        Oc2Credential("openai", Oc2OAuth(InAnHour));
        _api.Reply = (HttpStatusCode.OK, CodexBody);

        var codex = Codex();
        codex.Harnesses.ShouldBe(["opencode", "opencode2"]);
        codex.Source.ShouldBe("OpenCode");
        await codex.ReadAsync(default);
        _api.Requests.Single().Authorization.ShouldBe("Bearer oc1-secret");
    }

    [Fact]
    public async Task codex_with_an_expired_opencode2_token_is_unreadable_not_signed_out_and_asks_nothing()
    {
        Oc2Credential("openai", Oc2OAuth(_time.GetUtcNow().AddMinutes(-1).ToUnixTimeMilliseconds()));

        var reading = await Codex().ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Unreadable);
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task codex_ignores_an_inactive_row_a_missing_table_and_a_garbled_value()
    {
        (await Codex().HasSignInAsync(default)).ShouldBeFalse();   // no database at all
        Oc2Credential("openai", Oc2OAuth(InAnHour), active: false);
        (await Codex().HasSignInAsync(default)).ShouldBeFalse();
        Oc2Credential("openai", "{ not json");
        (await Codex().HasSignInAsync(default)).ShouldBeFalse();

        var other = Path.Combine(_home, "no-table.db");
        using (var connection = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x TEXT)";
            command.ExecuteNonQuery();
        }
        _env["OPENCODE_DB"] = other;
        (await Codex().HasSignInAsync(default)).ShouldBeFalse();
    }

    [Fact]
    public async Task codex_honors_opencode_db_and_never_writes_to_it()
    {
        var custom = Path.Combine(_home, "custom", "opencode.db");
        Oc2Credential("openai", Oc2OAuth(InAnHour), db: custom);
        _env["OPENCODE_DB"] = custom;
        var before = File.ReadAllBytes(custom);

        (await Codex().HasSignInAsync(default)).ShouldBeTrue();

        File.ReadAllBytes(custom).ShouldBe(before);
    }

    [Fact]
    public async Task openrouter_reads_the_opencode2_key_and_lists_both_harnesses()
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"or-oc1"}}""");
        Oc2Credential("openrouter", """{"type":"key","key":"or-oc2"}""");
        _api.Reply = (HttpStatusCode.OK, """{"data":{"usage":1.5,"limit":10,"limit_remaining":8.5}}""");

        var provider = OpenRouter();
        provider.Harnesses.ShouldBe(["opencode", "opencode2"]);
        var reading = await provider.ReadAsync(default);

        reading.State.ShouldBe(UsageStates.Ok);
        reading.Credits!.Used.ShouldBe(1.5);

        File.Delete(Path.Combine(_home, ".local/share/opencode/auth.json"));
        provider = OpenRouter();
        provider.Harnesses.ShouldBe(["opencode2"]);
        await provider.ReadAsync(default);
        _api.Requests.Last().Authorization.ShouldBe("Bearer or-oc2");
    }

    private sealed class FakeApi : IHttpClientFactory
    {
        public (HttpStatusCode Status, string Body) Reply { get; set; } = (HttpStatusCode.OK, "{}");
        public bool Throw { get; set; }
        public List<Asked> Requests { get; } = [];

        public sealed record Asked(string Uri, string? Authorization, Dictionary<string, string> Headers);

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(FakeApi api) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (api.Throw)
                    throw new HttpRequestException("no network");
                api.Requests.Add(new Asked(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
                return Task.FromResult(new HttpResponseMessage(api.Reply.Status) { Content = new StringContent(api.Reply.Body, Encoding.UTF8, "application/json") });
            }
        }
    }

    [Fact]
    public void codex_account_id_comes_from_the_auth_file_and_is_never_the_token()
    {
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"codex-secret","accountId":"acct-1"}}""");
        var first = Codex().AccountId;
        OpenCodeAuth("""{"openai":{"type":"oauth","access":"other-secret","accountId":"acct-2"}}""");
        var second = Codex().AccountId;

        first.ShouldBe("acct-1");
        second.ShouldBe("acct-2");
        AccountKey.For("codex", first).ShouldBe(AccountKey.For("codex", "acct-1"));
        AccountKey.For("codex", first).ShouldNotBe(AccountKey.For("codex", second));
        AccountKey.For("codex", first)!.ShouldNotContain("secret");
    }

    [Fact]
    public void openrouter_has_no_account_id()
    {
        OpenCodeAuth("""{"openrouter":{"type":"api","key":"or-secret"}}""");

        ((IUsageProvider)OpenRouter()).AccountId.ShouldBeNull();
    }

    [Fact]
    public void claude_has_no_account_id_unless_the_file_names_one()
    {
        ClaudeSignIn();
        Claude().AccountId.ShouldBeNull();

        Write(".claude/.credentials.json", """{"claudeAiOauth":{"accessToken":"claude-secret","accountUuid":"uuid-9"}}""");
        Claude().AccountId.ShouldBe("uuid-9");
    }

    [Fact]
    public async Task copilot_reading_carries_the_login_as_the_account_id()
    {
        CopilotReplies("""{"authenticated":true,"login":"octocat","plan":"pro","resetAt":"2026-11-01T00:00:00Z","quotas":[{"id":"premium_interactions","entitlement":300,"remaining":150,"percentRemaining":50,"unlimited":false}]}""");

        var reading = await Copilot().ReadAsync(default);

        reading.AccountId.ShouldBe("octocat");
        AccountKey.For("copilot", reading.AccountId).ShouldBe(AccountKey.For("copilot", "octocat"));
    }
}
