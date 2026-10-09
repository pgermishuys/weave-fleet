using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Checks every harness once at startup, in the background, so the first harness list a page asks for is already
/// there. A failed check is logged by <see cref="HarnessAvailabilityCache"/>; the first read then checks again.
/// </summary>
public sealed class HarnessAvailabilityWarmupHostedService(HarnessAvailabilityCache availability) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = availability.GetAsync(fresh: false, CancellationToken.None)
            .ContinueWith(check => _ = check.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
