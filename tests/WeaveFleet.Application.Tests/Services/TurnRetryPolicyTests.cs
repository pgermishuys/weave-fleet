using System.Globalization;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Services;

public sealed class TurnRetryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T01:51:43Z", CultureInfo.InvariantCulture);

    private static TurnError Error(string? kind, DateTimeOffset? retryAt = null)
        => new() { Name = "APIError", Message = "limit", Kind = kind, RetryAt = retryAt };

    [Fact]
    public void Only_limits_are_tried_again()
        => TurnRetryPolicy.Plan(Error(null), 1, Now).ShouldBeNull();

    [Fact]
    public void A_usage_limit_goes_half_a_minute_after_its_reset()
        => TurnRetryPolicy.Plan(Error(TurnErrorKinds.UsageLimit, Now.AddHours(3)), 1, Now)
            .ShouldBe((Now.AddHours(3).AddSeconds(30), true));

    [Fact]
    public void A_retry_after_that_soon_still_waits_a_moment()
        => TurnRetryPolicy.Plan(Error(TurnErrorKinds.RateLimit, Now.AddSeconds(2)), 1, Now)
            .ShouldBe((Now + TurnRetryPolicy.ShortestWait, true));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 30)]
    public void A_rate_limit_without_a_time_waits_longer_each_attempt(int attempt, int minutes)
        => TurnRetryPolicy.Plan(Error(TurnErrorKinds.RateLimit), attempt, Now).ShouldBe((Now.AddMinutes(minutes), false));

    [Fact]
    public void A_usage_limit_without_a_reset_is_looked_at_every_quarter_hour_or_more()
    {
        TurnRetryPolicy.Plan(Error(TurnErrorKinds.UsageLimit), 1, Now).ShouldBe((Now.AddMinutes(15), false));
        TurnRetryPolicy.Plan(Error(TurnErrorKinds.UsageLimit), 5, Now).ShouldBe((Now.AddMinutes(60), false));
    }

    [Fact]
    public void Not_after_the_last_attempt()
        => TurnRetryPolicy.Plan(Error(TurnErrorKinds.Overloaded), TurnRetryPolicy.MaxAttempts + 1, Now).ShouldBeNull();
}
