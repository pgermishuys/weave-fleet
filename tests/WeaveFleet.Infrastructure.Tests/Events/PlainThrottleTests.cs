using System.Globalization;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Events;

namespace WeaveFleet.Infrastructure.Tests.Events;

/// <summary>A plain 429 / overloaded failure (no usage-window words) is a short throttle, never a spent usage window.</summary>
public sealed class PlainThrottleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T10:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("APIError 429", "429 Too Many Requests", 429)]
    [InlineData("APIError", "Request failed with status code 429", 429)]
    [InlineData("provider.rate-limit", "Rate limit exceeded", 429)]
    [InlineData("RateLimit", "Too many requests, slow down", 429)]
    [InlineData("Error", "429 {\"error\":{\"message\":\"Rate limit reached for requests\",\"type\":\"requests\"}}", 429)]
    [InlineData("Error", "Error: rate limited by upstream provider, retry later", null)]
    [InlineData("Error", "CAPIError: 429 Sorry, you've been rate-limited. Please try again in a moment.", 429)]
    [InlineData("Error", "429 Rate limit exceeded: too many requests per minute", 429)]
    [InlineData("Error", "RESOURCE_EXHAUSTED: Quota exceeded for requests per minute", 429)]
    [InlineData("Error", "429 Your request was throttled. The model is at capacity", 429)]
    [InlineData("Error", "You have exceeded the rate limit. Limit reached, try again soon", 429)]
    [InlineData("Error", "Rate limit: request limit per minute exceeded", 429)]
    public void A_plain_throttle_is_a_rate_limit_without_a_reset_time_and_waits_a_short_time(string name, string message, int? status)
    {
        var error = ProviderLimitReader.Classify(new TurnError { Name = name, Message = message }, Now, status);

        error.Kind.ShouldBe(TurnErrorKinds.RateLimit, message);
        error.RetryAt.ShouldBeNull();
        var plan = TurnRetryPolicy.Plan(error, 1, Now);
        plan.ShouldNotBeNull();
        (plan.Value.DueAt - Now).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("Overloaded")]
    [InlineData("529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")]
    [InlineData("503 Service Unavailable")]
    public void An_overload_is_not_a_usage_limit(string message)
    {
        var error = ProviderLimitReader.Classify(new TurnError { Name = "APIError", Message = message }, Now, message.StartsWith("503", StringComparison.Ordinal) ? 503 : null);
        error.Kind.ShouldBe(TurnErrorKinds.Overloaded);
    }
}
