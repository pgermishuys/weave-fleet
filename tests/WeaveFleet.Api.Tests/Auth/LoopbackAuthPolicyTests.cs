using WeaveFleet.Api.Auth;

namespace WeaveFleet.Api.Tests.Auth;

public sealed class LoopbackAuthPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("")]
    [InlineData(null)]
    public void Should_allow_loopback_auto_auth_when_bound_to_a_loopback_address(string? host)
    {
        var policy = new LoopbackAuthPolicy(host);

        policy.IsRemoteReachable.ShouldBeFalse();
        policy.AllowsLoopbackAutoAuth.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("192.168.1.13")]
    [InlineData("100.64.90.72")]
    [InlineData("andromeda.tail2a6243.ts.net")]
    public void Should_refuse_loopback_auto_auth_when_bound_to_a_remote_reachable_address(string host)
    {
        var policy = new LoopbackAuthPolicy(host);

        policy.IsRemoteReachable.ShouldBeTrue();
        policy.AllowsLoopbackAutoAuth.ShouldBeFalse();
    }
}
