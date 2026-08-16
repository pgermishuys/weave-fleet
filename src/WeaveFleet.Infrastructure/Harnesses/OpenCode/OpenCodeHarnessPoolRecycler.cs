using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Recycles idle pooled OpenCode instances.
/// </summary>
internal sealed class OpenCodeHarnessPoolRecycler : IHarnessPoolRecycler
{
    private readonly OpenCodeHarnessRuntime _runtime;

    public OpenCodeHarnessPoolRecycler(OpenCodeHarnessRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<int> RecycleIdleInstancesAsync(CancellationToken cancellationToken = default)
    {
        return _runtime.RecycleIdlePooledInstancesAsync(cancellationToken);
    }
}
