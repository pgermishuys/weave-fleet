using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Devices;

namespace WeaveFleet.Application.Tests.Devices;

public sealed class PairingCodeStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly PairingCodeStore _store;

    public PairingCodeStoreTests()
    {
        _store = new PairingCodeStore(_time);
    }

    [Fact]
    public void A_code_has_a_long_secret_and_a_typeable_form()
    {
        var code = _store.Create();

        code.Secret.Length.ShouldBe(43);
        code.ManualCode.ShouldMatch("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
        code.ExpiresAt.ShouldBe(_time.GetUtcNow() + PairingCodeStore.Lifetime);
    }

    [Fact]
    public void Either_form_finds_the_code_and_peeking_does_not_use_it_up()
    {
        var code = _store.Create();

        _store.Peek(code.Secret, null).ShouldNotBeNull();
        _store.Peek(null, code.ManualCode).ShouldNotBeNull();
        _store.Peek(null, code.ManualCode.Replace("-", "").ToLowerInvariant()).ShouldNotBeNull();
        _store.Peek("wrong", null).ShouldBeNull();
        _store.Peek(null, null).ShouldBeNull();
    }

    [Fact]
    public void A_code_is_used_once()
    {
        var code = _store.Create();

        _store.TryConsume(code.Secret, null).ShouldNotBeNull();

        _store.TryConsume(code.Secret, null).ShouldBeNull();
        _store.TryConsume(null, code.ManualCode).ShouldBeNull();
        _store.Peek(code.Secret, null).ShouldBeNull();
    }

    [Fact]
    public void The_manual_code_uses_up_the_same_code()
    {
        var code = _store.Create();

        _store.TryConsume(null, code.ManualCode).ShouldNotBeNull();

        _store.TryConsume(code.Secret, null).ShouldBeNull();
    }

    [Fact]
    public void A_code_expires_after_ten_minutes()
    {
        var code = _store.Create();

        _time.Advance(PairingCodeStore.Lifetime - TimeSpan.FromSeconds(1));
        _store.Peek(code.Secret, null).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromSeconds(1));
        _store.Peek(code.Secret, null).ShouldBeNull();
        _store.TryConsume(code.Secret, null).ShouldBeNull();
    }

    [Fact]
    public void At_most_five_codes_are_live_and_the_oldest_goes_first()
    {
        var codes = Enumerable.Range(0, PairingCodeStore.MaxLive + 1).Select(_ => _store.Create()).ToList();

        _store.Peek(codes[0].Secret, null).ShouldBeNull();
        foreach (var code in codes.Skip(1))
            _store.Peek(code.Secret, null).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData(" ab cd ef gh ", "ABCDEFGH")]
    [InlineData("oil0-1234", "01101234")]
    [InlineData("ABCD-EFG", null)]
    [InlineData("ABCD-EFGHJ", null)]
    [InlineData("ABCD-EFGU", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Typed_codes_are_read_forgivingly(string? typed, string? expected)
    {
        PairingCodeStore.NormalizeManualCode(typed).ShouldBe(expected);
    }
}
