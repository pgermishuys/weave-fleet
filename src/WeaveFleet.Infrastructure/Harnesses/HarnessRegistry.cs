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
        // In their own order (HarnessPresentation.Order), so every list of harnesses comes out the same.
        _harnesses = harnesses.OrderBy(h => h.Presentation.Order).ToList();
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
                ? RequireMinimumVersion(harness, runtime, await runtime.CheckAvailabilityAsync(ct).ConfigureAwait(false))
                : HarnessAvailability.NotWorking("No runtime registered.");
            return HarnessInfo.From(harness, availability, runtime?.GetSetup(availability));
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>A ready harness older than the runtime's minimum version can't start sessions until it's updated.</summary>
    internal static HarnessAvailability RequireMinimumVersion(IHarness harness, IHarnessRuntime runtime, HarnessAvailability availability)
    {
        if (!availability.Available || runtime.MinimumVersion is not { } minimum || availability.Version is not { } version)
            return availability;
        return HarnessVersion.IsOlder(version, minimum)
            ? HarnessAvailability.UpdateNeeded(
                $"Fleet needs {harness.DisplayName} {minimum} or newer. You have {version}.",
                version,
                availability.ExecutablePath)
            : availability;
    }
}
