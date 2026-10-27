using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api.Tests.Infrastructure;

public sealed class RemoteMachinesTimeoutTests
{
    /// <summary>
    /// A fresh test Fleet answered its first request in 7–9 s on a busy CPU, past the 5 s a Fleet waits for another
    /// machine, so the two-Fleet tests failed with "Couldn't reach falcon".
    /// </summary>
    [Fact]
    public void A_test_Fleet_waits_for_another_longer_than_a_fresh_one_takes_to_answer_on_a_busy_CPU()
    {
        using var fleet = new ApiWebApplicationFactory(authEnabled: false, tokenAuthEnabled: true);

        using var client = fleet.Services.GetRequiredService<IHttpClientFactory>().CreateClient(RemoteMachineService.HttpClientName);

        client.Timeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
    }
}
