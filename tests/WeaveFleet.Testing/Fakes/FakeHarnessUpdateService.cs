using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Testing.Fakes;

/// <summary>Stands in for harness updates, so tests never look versions up on npm or run an updater.</summary>
public sealed class FakeHarnessUpdateService : IHarnessUpdateService
{
    /// <summary>Returned for a harness by <see cref="DescribeAsync"/>; harnesses not listed get an empty state.</summary>
    public Dictionary<string, HarnessUpdateInfo> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="StartAsync"/> returns.</summary>
    public Result<HarnessUpdateJob> StartResult { get; set; } =
        new HarnessUpdateJob(HarnessUpdatePhases.Waiting, null, null, 0, null, null);

    public List<string> Started { get; } = [];
    public List<string> Dismissed { get; } = [];
    public List<bool> Described { get; } = [];

    public Task<IReadOnlyDictionary<string, HarnessUpdateInfo>> DescribeAsync(
        IReadOnlyList<HarnessInfo> harnesses,
        bool checkLatest,
        CancellationToken ct)
    {
        Described.Add(checkLatest);
        IReadOnlyDictionary<string, HarnessUpdateInfo> result = harnesses.ToDictionary(
            harness => harness.Type,
            harness => Updates.GetValueOrDefault(harness.Type) ?? new HarnessUpdateInfo(),
            StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }

    public Task<Result<HarnessUpdateJob>> StartAsync(string harnessType, CancellationToken ct)
    {
        Started.Add(harnessType);
        return Task.FromResult(StartResult);
    }

    public Result<Unit> Dismiss(string harnessType)
    {
        Dismissed.Add(harnessType);
        return Unit.Value;
    }
}
