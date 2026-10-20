using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Usage;
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
    private OpenRouterUsageProvider OpenRouter() => new(_api, Sources());

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

    /// <summary>A fake provider API: one canned reply, and a record of what was asked.</summary>
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
}
