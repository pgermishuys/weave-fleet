using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Collects all <see cref="IHarness"/> implementations from DI
/// and provides lookup + availability checking.
/// </summary>
public sealed class HarnessRegistry : IHarnessRegistry
{
    private readonly List<IHarness> _harnesses;

    public HarnessRegistry(IEnumerable<IHarness> harnesses)
    {
        _harnesses = harnesses.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<IHarness> GetAll() => _harnesses;

    /// <inheritdoc />
    public IHarness? GetByType(string harnessType) =>
        _harnesses.FirstOrDefault(h =>
            string.Equals(h.Type, harnessType, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
    {
        var results = new List<HarnessInfo>(_harnesses.Count);
        foreach (var harness in _harnesses)
        {
            var availability = await harness.CheckAvailabilityAsync(ct).ConfigureAwait(false);
            results.Add(new HarnessInfo(
                harness.Type,
                harness.DisplayName,
                availability.Available,
                availability.Reason,
                harness.Capabilities));
        }
        return results;
    }
}
