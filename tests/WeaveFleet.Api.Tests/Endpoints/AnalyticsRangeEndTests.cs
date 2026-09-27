using Shouldly;
using WeaveFleet.Api.Endpoints;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class AnalyticsRangeEndTests
{
    // The dashboard's 14-day spend and the Analytics page both end their range on today's date; read as midnight, today
    // was left out of every figure.
    [Fact]
    public void A_bare_date_ends_the_range_after_that_day()
    {
        AnalyticsEndpoints.ParseRangeEnd("2026-09-27").ShouldBe(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_timestamp_ends_the_range_where_it_says()
    {
        AnalyticsEndpoints.ParseRangeEnd("2026-09-27T12:30:00Z").ShouldBe(new DateTimeOffset(2026, 9, 27, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void No_end_is_no_limit()
    {
        AnalyticsEndpoints.ParseRangeEnd(null).ShouldBeNull();
        AnalyticsEndpoints.ParseRangeEnd("not a date").ShouldBeNull();
    }
}
