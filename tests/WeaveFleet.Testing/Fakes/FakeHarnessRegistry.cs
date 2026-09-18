using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarnessRegistry : IHarnessRegistry
{
    private readonly List<IHarness> _harnesses = [];
    private readonly List<IHarnessRuntime> _runtimes = [];

    public void Register(IHarness harness) => _harnesses.Add(harness);
    public void Register(IHarnessRuntime runtime) => _runtimes.Add(runtime);

    // ── Call tracking ────────────────────────────────────────────────────────

    public List<string> GetByTypeCalls { get; } = [];
    public List<string> GetRuntimeByTypeCalls { get; } = [];

    public IReadOnlyList<IHarness> GetAll() => [.. _harnesses];

    public IHarness? GetByType(string harnessType)
    {
        GetByTypeCalls.Add(harnessType);
        return _harnesses.FirstOrDefault(h => h.Type.Equals(harnessType, StringComparison.OrdinalIgnoreCase));
    }

    public IHarnessRuntime? GetRuntimeByType(string harnessType)
    {
        GetRuntimeByTypeCalls.Add(harnessType);
        return _runtimes.FirstOrDefault(r => r.HarnessType.Equals(harnessType, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
    {
        var results = new List<HarnessInfo>();
        foreach (var harness in _harnesses)
        {
            var runtime = GetRuntimeByType(harness.Type);
            if (runtime is null)
            {
                results.Add(HarnessInfo.From(harness.Type, harness.DisplayName, harness.Capabilities, HarnessAvailability.NotWorking("No runtime registered.")));
                continue;
            }

            var availability = await runtime.CheckAvailabilityAsync(ct);
            results.Add(HarnessInfo.From(harness.Type, harness.DisplayName, harness.Capabilities, availability, runtime.GetSetup(availability)));
        }
        return results;
    }
}
