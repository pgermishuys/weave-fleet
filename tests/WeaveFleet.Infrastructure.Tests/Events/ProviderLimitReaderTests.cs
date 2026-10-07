using System.Globalization;
using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Events;

namespace WeaveFleet.Infrastructure.Tests.Events;

/// <summary>
/// Which failures are a model provider's limit, and when the provider said a request can go again, from the shapes the
/// harnesses pass on.
/// </summary>
public sealed class ProviderLimitReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T01:51:43Z", CultureInfo.InvariantCulture);

    private static TurnError Classify(string name, string message, int? status = null, string? headers = null, string? body = null)
        => ProviderLimitReader.Classify(
            new TurnError { Name = name, Message = message },
            Now,
            status,
            headers is null ? null : JsonDocument.Parse(headers).RootElement,
            body);

    [Fact]
    public void Claudes_session_limit_is_a_usage_limit_that_resets_at_the_time_it_names()
    {
        var error = Classify("APIError", "You've hit your session limit · resets 3:43am (UTC)", 429);

        error.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        error.RetryAt.ShouldBe(DateTimeOffset.Parse("2026-10-07T03:43:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_reset_time_that_has_passed_today_is_tomorrows()
    {
        var error = Classify("APIError", "You've hit your weekly limit · resets 1am (UTC)");

        error.RetryAt.ShouldBe(DateTimeOffset.Parse("2026-10-08T01:00:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_reset_with_a_date_and_a_zone_is_read_in_that_zone()
    {
        var error = Classify("APIError", "You've hit your usage limit. Try again at Oct 9th, 2026 3:05 PM.");

        error.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        var expected = new DateTimeOffset(new DateTime(2026, 10, 9, 15, 5, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 9, 15, 5, 0)));
        error.RetryAt.ShouldBe(expected);
    }

    [Fact]
    public void Claudes_server_side_throttle_is_a_rate_limit_not_a_usage_limit()
    {
        var error = Classify(
            "APIError",
            "API Error: Server is temporarily limiting requests (not your usage limit) · Number of request tokens has exceeded your per-minute rate limit",
            429);

        error.Kind.ShouldBe(TurnErrorKinds.RateLimit);
        error.RetryAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("You're out of usage credits. Switch to another model")]
    [InlineData("You exceeded your current quota, please check your plan and billing details. insufficient_quota")]
    [InlineData("You've hit your monthly spend limit.")]
    [InlineData("Your credit balance is too low to access the Anthropic API.")]
    public void Limits_a_wait_doesnt_get_past_arent_limits(string message)
        => Classify("APIError", message, 429).Kind.ShouldBeNull();

    [Theory]
    [InlineData("Connection reset by server", null)]
    [InlineData("prompt is too long: 210000 tokens > 200000 maximum", 400)]
    [InlineData("Invalid API key", 401)]
    public void Other_failures_arent_limits(string message, int? status)
        => Classify("APIError", message, status).Kind.ShouldBeNull();

    [Fact]
    public void Codex_usage_limit_body_says_when_it_resets()
    {
        var error = Classify(
            "APIError",
            "The usage limit has been reached",
            429,
            body: """{"error":{"type":"usage_limit_reached","message":"The usage limit has been reached","plan_type":"plus","resets_in_seconds":7380}}""");

        error.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        error.RetryAt.ShouldBe(Now.AddSeconds(7380));
    }

    [Fact]
    public void Codex_headers_say_when_the_used_up_window_resets()
    {
        var error = Classify(
            "APIError",
            "Too many requests",
            429,
            headers: """{"x-codex-primary-used-percent":"100","x-codex-primary-reset-after-seconds":"600","x-codex-secondary-used-percent":"42","x-codex-secondary-reset-after-seconds":"90000"}""");

        error.RetryAt.ShouldBe(Now.AddSeconds(600));
    }

    [Fact]
    public void Retry_after_headers_are_read_in_seconds_milliseconds_or_as_a_date()
    {
        Classify("APIError", "Rate limited", 429, headers: """{"retry-after":"30"}""").RetryAt.ShouldBe(Now.AddSeconds(30));
        Classify("APIError", "Rate limited", 429, headers: """{"Retry-After-Ms":"1500"}""").RetryAt.ShouldBe(Now.AddMilliseconds(1500));
        Classify("APIError", "Rate limited", 429, headers: """{"retry-after":"Wed, 07 Oct 2026 02:00:00 GMT"}""")
            .RetryAt.ShouldBe(DateTimeOffset.Parse("2026-10-07T02:00:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_rejected_claude_subscription_window_resets_when_its_header_says()
        => Classify(
                "APIError",
                "This request would exceed your account's rate limit.",
                429,
                headers: """{"anthropic-ratelimit-unified-status":"rejected","anthropic-ratelimit-unified-reset":"1791344624"}""")
            .RetryAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791344624));

    [Fact]
    public void OpenAIs_per_minute_resets_are_durations()
        => Classify("APIError", "Rate limit reached for gpt-5 in organization org-x on tokens per min", 429,
                headers: """{"x-ratelimit-reset-requests":"1s","x-ratelimit-reset-tokens":"6m0s"}""")
            .RetryAt.ShouldBe(Now.AddMinutes(6));

    [Fact]
    public void Words_that_say_how_long_to_wait_are_read()
    {
        Classify("APIError", "Rate limit reached. Please try again in 20s.").RetryAt.ShouldBe(Now.AddSeconds(20));
        Classify("GoUsageLimitError", "Go usage limit reached. It will reset in 2 hours 5 minutes.")
            .RetryAt.ShouldBe(Now.AddHours(2).AddMinutes(5));
    }

    [Fact]
    public void A_reset_further_out_than_a_week_and_a_day_isnt_believed()
        => Classify("APIError", "Rate limited", 429, headers: """{"retry-after":"1000000"}""").RetryAt.ShouldBeNull();

    [Theory]
    [InlineData("RateLimit", "Too many requests", TurnErrorKinds.RateLimit)]
    [InlineData("QuotaExceeded", "The usage limit has been reached", TurnErrorKinds.UsageLimit)]
    [InlineData("rate_limit_error", "Number of requests has exceeded your rate limit", TurnErrorKinds.RateLimit)]
    [InlineData("overloaded_error", "Overloaded", TurnErrorKinds.Overloaded)]
    public void Typed_names_say_the_limit(string name, string message, string kind)
        => Classify(name, message).Kind.ShouldBe(kind);

    [Fact]
    public void A_529_or_503_is_an_overload()
    {
        Classify("provider", "The model is overloaded.", 529).Kind.ShouldBe(TurnErrorKinds.Overloaded);
        Classify("APIError", "upstream error", 503).Kind.ShouldBe(TurnErrorKinds.Overloaded);
    }

    [Fact]
    public void OpenCodes_api_error_passes_on_its_status_headers_and_body()
    {
        var payload = JsonDocument.Parse(
            """
            {
              "sessionID": "oc-1",
              "error": {
                "name": "APIError",
                "data": {
                  "message": "The usage limit has been reached",
                  "statusCode": 429,
                  "isRetryable": false,
                  "responseHeaders": { "retry-after": "120" },
                  "responseBody": "{\"error\":{\"type\":\"usage_limit_reached\"}}"
                }
              }
            }
            """).RootElement;

        var error = HarnessErrorReader.TryReadFromPayload(payload, Now).ShouldNotBeNull();

        error.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        error.RetryAt.ShouldBe(Now.AddSeconds(120));
    }

    [Fact]
    public void An_adapter_that_knows_the_limit_says_so_itself()
    {
        var payload = JsonDocument.Parse(
            """{"sessionID":"s","error":{"name":"APIError 429","message":"You've hit your session limit","kind":"usage_limit","retryAt":"2026-10-07T03:43:44+00:00"}}""").RootElement;

        var error = HarnessErrorReader.TryReadFromPayload(payload, Now).ShouldNotBeNull();

        error.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        error.RetryAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791344624));
    }

    [Theory]
    [InlineData("6m0s", 360_000)]
    [InlineData("1.5s", 1_500)]
    [InlineData("250ms", 250)]
    [InlineData("2h 5m", 7_500_000)]
    [InlineData("1 hour and 5 minutes", 3_900_000)]
    public void Durations(string text, double milliseconds)
        => ProviderLimitReader.ParseDuration(text).ShouldBe(TimeSpan.FromMilliseconds(milliseconds));
}
