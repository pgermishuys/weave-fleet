using Shouldly;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Tests.Runtimes;

public sealed class BunVersionTests
{
    [Theory]
    [InlineData("1.4.2", 1, 4, 2, null)]
    [InlineData("1.4.3-canary.20+abc123", 1, 4, 3, "canary.20")]
    [InlineData("0.0.0", 0, 0, 0, null)]
    [InlineData("1.4.2+build.5", 1, 4, 2, null)]
    [InlineData("10.20.30-rc.1", 10, 20, 30, "rc.1")]
    public void Parses_valid_versions(string text, int major, int minor, int patch, string? pre)
    {
        BunVersion.TryParse(text, out var version).ShouldBeTrue();
        version.ShouldBe(new BunVersion(major, minor, patch, pre));
    }

    [Theory]
    [InlineData("v1.4.2")]
    [InlineData(" 1.4.2")]
    [InlineData("1.4.2 ")]
    [InlineData("1.4.2\n")]
    [InlineData("1.4")]
    [InlineData("1.4.2.1")]
    [InlineData("01.4.2")]
    [InlineData("1.04.2")]
    [InlineData("1.4.2-")]
    [InlineData("1.4.2-01")]
    [InlineData("bun 1.4.2")]
    [InlineData("")]
    [InlineData("99999999999.0.0")]
    [InlineData("1.99999999999.0")]
    [InlineData("-1.4.2")]
    public void Rejects_invalid_versions(string text) => BunVersion.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Rejects_null_and_very_long_text()
    {
        BunVersion.TryParse(null, out _).ShouldBeFalse();
        BunVersion.TryParse(new string('1', 200), out _).ShouldBeFalse();
        BunVersion.TryParse("1.4.2-" + new string('a', 200), out _).ShouldBeFalse();
    }

    [Fact]
    public void Parse_throws_for_text_that_is_not_a_version() =>
        Should.Throw<FormatException>(() => BunVersion.Parse("nope"));

    [Theory]
    [InlineData("1.4.3-canary.20", "1.4.3")]
    [InlineData("1.4.9", "1.4.10")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.4.2", "2.0.0")]
    [InlineData("1.4.3-1", "1.4.3-alpha")]
    [InlineData("1.4.3-alpha.2", "1.4.3-alpha.10")]
    [InlineData("1.4.3-alpha", "1.4.3-alpha.1")]
    [InlineData("1.4.3-alpha", "1.4.3-beta")]
    public void Orders_older_before_newer(string older, string newer)
    {
        var a = BunVersion.Parse(older);
        var b = BunVersion.Parse(newer);
        a.CompareTo(b).ShouldBeLessThan(0);
        b.CompareTo(a).ShouldBeGreaterThan(0);
        a.IsOlderThan(b).ShouldBeTrue();
        b.IsOlderThan(a).ShouldBeFalse();
        (a < b).ShouldBeTrue();
        (a <= b).ShouldBeTrue();
        (b > a).ShouldBeTrue();
        (b >= a).ShouldBeTrue();
    }

    [Fact]
    public void Build_metadata_is_ignored_when_comparing()
    {
        var a = BunVersion.Parse("1.4.2+abc");
        var b = BunVersion.Parse("1.4.2+def");
        a.CompareTo(b).ShouldBe(0);
        a.ShouldBe(b);
    }

    [Theory]
    [InlineData("1.4.2")]
    [InlineData("0.0.0")]
    [InlineData("1.4.3-canary.20")]
    public void ToString_round_trips(string text) => BunVersion.Parse(text).ToString().ShouldBe(text);

    [Fact]
    public void ToString_drops_build_metadata() =>
        BunVersion.Parse("1.4.3-canary.20+abc").ToString().ShouldBe("1.4.3-canary.20");
}
