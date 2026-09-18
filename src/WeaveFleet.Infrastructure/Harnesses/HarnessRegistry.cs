using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

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
            var availability = runtime is not null
                ? await runtime.CheckAvailabilityAsync(ct).ConfigureAwait(false)
                : HarnessAvailability.NotWorking("No runtime registered.");
            return HarnessInfo.From(harness.Type, harness.DisplayName, harness.Capabilities, availability);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
