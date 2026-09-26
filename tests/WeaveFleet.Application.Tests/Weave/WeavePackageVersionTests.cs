using Shouldly;
using WeaveFleet.Application.Weave;

namespace WeaveFleet.Application.Tests.Weave;

public sealed class WeavePackageVersionTests
{
    [Theory]
    [InlineData("0.2.0-next.3", "0.1.0")]
    [InlineData("0.2.0", "0.2.0-next.3")]
    [InlineData("0.2.0-next.10", "0.2.0-next.3")]
    [InlineData("0.2.0-next.1", "0.2.0-beta.9")]
    [InlineData("0.2.0-next.1.1", "0.2.0-next.1")]
    [InlineData("1.0.0", "0.99.99")]
    public void the_first_is_newer(string newer, string older)
    {
        WeavePackageVersion.Compare(newer, older).ShouldBePositive();
        WeavePackageVersion.Compare(older, newer).ShouldBeNegative();
    }

    [Fact]
    public void the_same_version_compares_equal()
        => WeavePackageVersion.Compare("0.2.0-next.3", "0.2.0-next.3").ShouldBe(0);

    [Fact]
    public void the_newest_tag_wins_and_a_missing_one_is_skipped()
    {
        // npm today: latest 0.1.0 predates OpenCode 2's agents, next 0.2.0-next.3 has them.
        WeavePackageVersion.Newest("0.1.0", "0.2.0-next.3").ShouldBe("0.2.0-next.3");
        WeavePackageVersion.Newest("0.2.0", "0.2.0-next.3").ShouldBe("0.2.0");
        WeavePackageVersion.Newest("0.1.2", null).ShouldBe("0.1.2");
        WeavePackageVersion.Newest(null, null).ShouldBeNull();
    }
}
