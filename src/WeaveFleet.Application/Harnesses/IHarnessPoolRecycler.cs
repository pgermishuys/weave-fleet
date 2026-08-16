namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// Recycles idle pooled harness instances to pick up configuration changes.
/// </summary>
public interface IHarnessPoolRecycler
{
    /// <summary>
    /// Recycles all idle (RefCount == 0) pooled instances.
    /// Active sessions are NOT interrupted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of instances recycled.</returns>
    Task<int> RecycleIdleInstancesAsync(CancellationToken cancellationToken = default);
}
