using WeaveFleet.Application.Automations;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Testing.Fakes;

/// <summary>A built-in automation host that is on or off as the test says and records the runs it was handed.</summary>
public sealed class FakeBuiltinAutomations : IBuiltinAutomations
{
    private readonly List<string> _runs = [];

    public bool On { get; set; } = true;
    public AutomationExecutionOutcome Outcome { get; set; } = new(null, null, null, Summary: "Reading alpha…");

    public IReadOnlyList<string> RunIds
    {
        get
        {
            lock (_runs)
                return [.. _runs];
        }
    }

    public Task<bool> IsOnAsync(Automation automation) => Task.FromResult(On);

    public Task<AutomationExecutionOutcome> RunAsync(Automation automation, AutomationRun run, CancellationToken ct = default)
    {
        lock (_runs)
            _runs.Add(run.Id);
        return Task.FromResult(Outcome);
    }
}
