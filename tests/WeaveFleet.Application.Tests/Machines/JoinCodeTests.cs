using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

public sealed class JoinCodeTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly JoinCodeStore _store;

    public JoinCodeTests()
    {
        _store = new JoinCodeStore(_time);
    }

    [Fact]
    public void A_join_code_lasts_30_minutes_where_a_pairing_code_lasts_10()
    {
        var code = _store.Create();

        JoinCodeStore.Lifetime.ShouldBe(TimeSpan.FromMinutes(30));
        code.ExpiresAt.ShouldBe(_time.GetUtcNow() + TimeSpan.FromMinutes(30));
        code.ManualCode.ShouldMatch("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
    }

    [Fact]
    public void A_join_code_is_good_until_30_minutes_have_passed()
    {
        var code = _store.Create();

        _time.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        _store.IsLive(code.ManualCode).ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(1));
        _store.IsLive(code.ManualCode).ShouldBeFalse();
        _store.TryConsume(code.ManualCode).ShouldBeFalse("an expired code can't be used");
    }

    [Fact]
    public void A_join_code_works_for_one_machine()
    {
        var code = _store.Create();

        _store.TryConsume(code.ManualCode).ShouldBeTrue();
        _store.TryConsume(code.ManualCode).ShouldBeFalse();
        _store.IsLive(code.ManualCode).ShouldBeFalse();
    }

    [Fact]
    public void A_code_that_was_never_made_is_refused_and_does_not_use_up_the_real_one()
    {
        var code = _store.Create();

        _store.TryConsume("ABCD-EFGH").ShouldBeFalse();
        _store.TryConsume(null).ShouldBeFalse();
        _store.TryConsume("").ShouldBeFalse();
        _store.TryConsume("not a code").ShouldBeFalse();

        _store.IsLive(code.ManualCode).ShouldBeTrue();
    }

    [Fact]
    public void A_typed_code_is_read_the_way_a_pairing_code_is()
    {
        var code = _store.Create();

        _store.IsLive(code.ManualCode.Replace("-", "").ToLowerInvariant()).ShouldBeTrue();
    }

    [Fact]
    public void A_join_code_is_not_a_pairing_code()
    {
        var pairing = new WeaveFleet.Application.Devices.PairingCodeStore(_time);
        var code = _store.Create();

        pairing.Peek(null, code.ManualCode).ShouldBeNull("a pairing store doesn't know join codes");
    }

    [Fact]
    public void A_join_line_is_the_addresses_then_the_code()
    {
        var (target, error) = JoinTarget.Parse("'http://100.64.1.7:6262,http://192.168.1.20:6262#J8RT-4WXN'");

        error.ShouldBeNull();
        target!.Addresses.ShouldBe(["http://100.64.1.7:6262", "http://192.168.1.20:6262"]);
        target.Code.ShouldBe("J8RT-4WXN");
        JoinTarget.Format(target.Addresses, target.Code).ShouldBe("http://100.64.1.7:6262,http://192.168.1.20:6262#J8RT-4WXN");
    }

    [Fact]
    public void An_address_without_a_scheme_is_http_and_a_code_may_be_typed_loosely()
    {
        var (target, error) = JoinTarget.Parse("studio.tail4f2a.ts.net:6262#j8rt4wxn");

        error.ShouldBeNull();
        target!.Addresses.ShouldBe(["http://studio.tail4f2a.ts.net:6262"]);
        target.Code.ShouldBe("J8RT-4WXN");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("J8RT-4WXN")]
    [InlineData("http://100.64.1.7:6262")]
    [InlineData("http://100.64.1.7:6262#")]
    [InlineData("#J8RT-4WXN")]
    [InlineData("http://100.64.1.7:6262#short")]
    [InlineData("ftp://x#J8RT-4WXN")]
    public void Anything_else_is_not_a_join_line_and_says_what_one_looks_like(string? text)
    {
        var (target, error) = JoinTarget.Parse(text);

        target.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}
