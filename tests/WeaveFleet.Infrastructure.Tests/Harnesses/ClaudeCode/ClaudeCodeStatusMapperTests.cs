using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// <see cref="ClaudeCodeMapper"/>'s reading of what Claude Code says about what it's doing, for the shapes the recorded
/// fixtures don't cover. Field names and values are from Claude Code 2.1.290's own schema for each line.
/// </summary>
public sealed class ClaudeCodeStatusMapperTests
{
    [Theory]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":3,"max_retries":10,"retry_delay_ms":12000,"error_status":529,"error":"overloaded"}""", "API overloaded (529)")]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":429,"error":"rate_limit"}""", "Rate limited (429)")]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":500,"error":"server_error"}""", "API error (500)")]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":null,"error":"unknown"}""", "Connection error")]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":401,"error":"authentication_failed"}""", "Not signed in (401)")]
    public void A_retry_says_why_in_words(string line, string reason)
    {
        var system = Parse<ClaudeCodeSystemMessage>(line);

        ClaudeCodeMapper.DescribeRetry(system).ShouldBe(reason);
        var status = ClaudeCodeMapper.TryMapRetry(system, "s1").ShouldNotBeNull().Payload!.Value.GetProperty("status");
        status.GetProperty("type").GetString().ShouldBe(ActivityStatuses.Retry);
        status.GetProperty("reason").GetString().ShouldBe(reason);
    }

    [Fact]
    public void Other_system_lines_are_not_retries()
        => ClaudeCodeMapper.TryMapRetry(Parse<ClaudeCodeSystemMessage>("""{"type":"system","subtype":"status","status":"requesting"}"""), "s1").ShouldBeNull();

    [Fact]
    public void A_compact_boundary_without_post_tokens_still_gives_a_divider()
    {
        var part = ClaudeCodeMapper.ToCompactionPart(Parse<ClaudeCodeSystemMessage>(
            """{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":181000}}"""))
            .ShouldNotBeNull();

        part.Trigger.ShouldBe(ContextCompactionTriggers.Auto);
        part.TokensBefore.ShouldBe(181_000);
        part.TokensAfter.ShouldBeNull();
    }

    [Fact]
    public void A_user_line_that_isnt_synthetic_is_not_a_summary()
        => ClaudeCodeMapper.ReadCompactionSummary(Parse<ClaudeCodeUserMessage>(
            """{"type":"user","message":{"role":"user","content":"This session is being continued from a previous conversation.\n\nSummary:\nx"}}""")).ShouldBeNull();

    [Fact]
    public void A_rejected_rate_limit_marks_its_window_used_up()
    {
        var report = ClaudeCodeMapper.ToUsageLimits(Parse<ClaudeCodeRateLimitEvent>(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791344624,"rateLimitType":"five_hour","utilization":1,"unifiedWindows":{"five_hour":{"utilization":1,"resetsAt":1791344624},"seven_day":{"utilization":0.4,"resetsAt":1791737124}}}}""")).ShouldNotBeNull();

        var windows = report.Windows.ToDictionary(w => w.Window);
        windows[UsageLimitWindows.FiveHour].Status.ShouldBe(UsageLimitStatuses.Rejected);
        windows[UsageLimitWindows.SevenDay].Status.ShouldBe(UsageLimitStatuses.Allowed);
    }

    [Fact]
    public void A_model_scoped_window_outside_the_unified_ones_is_kept()
    {
        var report = ClaudeCodeMapper.ToUsageLimits(Parse<ClaudeCodeRateLimitEvent>(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1791737124,"rateLimitType":"seven_day_opus","utilization":0.91}}""")).ShouldNotBeNull();

        var window = report.Windows.ShouldHaveSingleItem();
        window.Window.ShouldBe(UsageLimitWindows.SevenDayOpus);
        window.Utilization.ShouldBe(0.91);
        window.Status.ShouldBe(UsageLimitStatuses.Warning);
    }

    [Fact]
    public void A_rate_limit_event_with_no_info_reports_nothing()
        => ClaudeCodeMapper.ToUsageLimits(Parse<ClaudeCodeRateLimitEvent>("""{"type":"rate_limit_event"}""")).ShouldBeNull();

    [Fact]
    public void Get_usage_with_limits_gives_the_windows_in_fractions()
    {
        // The shape Claude Code 2.1.290's schema gives for get_usage on a claude.ai login (0–100, ISO times). Not recorded:
        // the only login on hand answers through a gateway, which gives none (the next test).
        using var answer = JsonDocument.Parse("""
            {"session":{"total_cost_usd":0},"subscription_type":"max","rate_limits_available":true,
             "rate_limits":{"five_hour":{"utilization":82,"resets_at":"2026-10-07T14:05:00Z"},"seven_day":{"utilization":63,"resets_at":"2026-10-12T09:00:00Z"},"seven_day_opus":null},
             "behaviors":null}
            """);

        var report = ClaudeCodeMapper.ToUsageLimits(answer.RootElement).ShouldNotBeNull();

        var windows = report.Windows.ToDictionary(w => w.Window);
        windows.Keys.Order().ShouldBe([UsageLimitWindows.FiveHour, UsageLimitWindows.SevenDay]);
        windows[UsageLimitWindows.FiveHour].Utilization.ShouldNotBeNull().ShouldBe(0.82, 1e-9);
        windows[UsageLimitWindows.FiveHour].ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 7, 14, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Get_usage_through_a_gateway_gives_none()
    {
        // Recorded from claude 2.1.290 through the user's gateway (ANTHROPIC_BASE_URL with an auth token).
        using var answer = JsonDocument.Parse("""
            {"session":{"total_cost_usd":0,"total_api_duration_ms":0,"total_duration_ms":435,"total_lines_added":0,"total_lines_removed":0,"model_usage":{}},"subscription_type":null,"rate_limits_available":false,"rate_limits":null,"behaviors":null}
            """);

        ClaudeCodeMapper.ToUsageLimits(answer.RootElement).ShouldBeNull();
    }

    [Fact]
    public void A_messages_step_counts_fresh_input_and_splits_out_its_thinking()
    {
        var usage = new ClaudeCodeUsage
        {
            InputTokens = 10,
            CacheCreationInputTokens = 3028,
            CacheReadInputTokens = 31981,
            OutputTokens = 169,
            OutputTokensDetails = new ClaudeCodeOutputDetails { ThinkingTokens = 92 },
        };

        var step = ClaudeCodeMapper.ToStepFinish(usage, "tool_use", cost: 0.5);

        step.TokensInput.ShouldBe(3038);
        step.TokensOutput.ShouldBe(77);
        step.TokensReasoning.ShouldBe(92);
        step.Reason.ShouldBe("tool_use");
        step.Cost.ShouldBe(0.5);
    }

    private static T Parse<T>(string line) where T : ClaudeCodeStreamMessage
        => JsonSerializer.Deserialize(line, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage).ShouldBeOfType<T>();
}
