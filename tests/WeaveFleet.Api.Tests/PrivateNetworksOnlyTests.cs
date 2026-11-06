using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace WeaveFleet.Api.Tests;

/// <summary>A Fleet set up for Tailscale listens on every network but answers only on private ones.</summary>
public sealed class PrivateNetworksOnlyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.42")]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.0.3")]
    [InlineData("100.88.14.3")]
    [InlineData("fd7a:115c:a1e0::3")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.42")]
    public void Requests_on_private_and_tailscale_addresses_are_answered(string local)
        => PrivateNetworksOnly.Allows(IPAddress.Parse(local)).ShouldBeTrue();

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("8.8.8.8")]
    [InlineData("100.128.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:203.0.113.9")]
    public void Requests_on_public_addresses_are_dropped(string local)
        => PrivateNetworksOnly.Allows(IPAddress.Parse(local)).ShouldBeFalse();

    [Theory]
    [InlineData("203.0.113.9", false)]
    [InlineData("192.168.1.42", true)]
    [InlineData("100.88.14.3", true)]
    public async Task A_request_on_a_public_address_is_dropped_without_an_answer(string local, bool answered)
    {
        var lifetime = new Lifetime();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Connection.LocalIpAddress = IPAddress.Parse(local);
        var reached = false;

        await PrivateNetworksOnly.FilterAsync(context, () =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        reached.ShouldBe(answered);
        lifetime.Aborted.ShouldBe(!answered);
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}
