using Shouldly;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>This machine's MagicDNS name, read from <c>tailscale status --json</c> → <c>Self.DNSName</c>.</summary>
public sealed class TailnetNameTests
{
    private sealed class CountingTailscale(string? json) : ITailscale
    {
        public int Calls { get; private set; }

        public Task<string?> StatusJsonAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(json);
        }
    }

    [Fact]
    public async Task The_name_has_its_trailing_dot_dropped()
    {
        var name = new TailnetName(new CountingTailscale("""{ "Self": { "HostName": "falcon", "DNSName": "falcon.tail9c2e.ts.net." } }"""));

        (await name.GetAsync(default)).ShouldBe("falcon.tail9c2e.ts.net");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "Peer": {} }""")]
    [InlineData("""{ "Self": { "DNSName": "" } }""")]
    [InlineData("""{ "Self": { "DNSName": 4 } }""")]
    public async Task Without_a_name_there_is_none(string? json)
        => (await new TailnetName(new CountingTailscale(json)).GetAsync(default)).ShouldBeNull();

    [Fact]
    public async Task The_answer_is_kept_for_a_minute()
    {
        var tailscale = new CountingTailscale("""{ "Self": { "DNSName": "falcon.tail9c2e.ts.net." } }""");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var name = new TailnetName(tailscale, clock);

        await name.GetAsync(default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await name.GetAsync(default);
        tailscale.Calls.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(31));
        await name.GetAsync(default);
        tailscale.Calls.ShouldBe(2);
    }
}
