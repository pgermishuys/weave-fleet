using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Collects all <see cref="IHarness"/> and <see cref="IHarnessRuntime"/> implementations from DI
/// and provides lookup + availability checking.
/// </summary>
public sealed class HarnessRegistry : IHarnessRegistry
{
    private readonly List<IHarness> _harnesses;
    private readonly List<IHarnessRuntime> _runtimes;

    public HarnessRegistry(IEnumerable<IHarness> harnesses, IEnumerable<IHarnessRuntime> runtimes)
    {
        _harnesses = harnesses.ToList();
        _runtimes = runtimes.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<IHarness> GetAll() => _harnesses;

    /// <inheritdoc />
    public IHarness? GetByType(string harnessType) =>
        _harnesses.FirstOrDefault(h =>
            string.Equals(h.Type, harnessType, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IHarnessRuntime? GetRuntimeByType(string harnessType) =>
        _runtimes.FirstOrDefault(r =>
            string.Equals(r.HarnessType, harnessType, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
    {
        var tasks = _harnesses.Select(async harness =>
        {
            var runtime = GetRuntimeByType(harness.Type);
            HarnessInfo info;
            if (runtime is not null)
            {
                var availability = await runtime.CheckAvailabilityAsync(ct).ConfigureAwait(false);
                info = new HarnessInfo(
                    harness.Type,
                    harness.DisplayName,
                    availability.Available,
                    UserEnabled: false,
                    availability.Reason,
                    harness.Capabilities);
            }
            else
            {
                info = new HarnessInfo(
                    harness.Type,
                    harness.DisplayName,
                    Available: false,
                    UserEnabled: false,
                    Reason: "No runtime registered.",
                    harness.Capabilities);
            }
            return info;
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
