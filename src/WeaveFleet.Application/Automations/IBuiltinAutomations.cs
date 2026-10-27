using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Automations;

/// <summary>
/// The automations Fleet owns (<see cref="Automation.Builtin"/>): it creates and removes them, and runs them itself rather
/// than through <see cref="IAutomationExecutor"/>. Dreaming is the first.
/// </summary>
public interface IBuiltinAutomations
{
    /// <summary>
    /// Whether the feature behind <paramref name="automation"/> is on for its owner. Off, it isn't listed, scheduled or run,
    /// so it costs nothing.
    /// </summary>
    Task<bool> IsOnAsync(Automation automation);

    /// <summary>
    /// Starts a run <see cref="AutomationRunService.BeginAsync"/> recorded. The outcome is skipped (with why) or carries a
    /// <see cref="AutomationExecutionOutcome.Summary"/>: the run then goes on in the background and settles itself.
    /// </summary>
    Task<AutomationExecutionOutcome> RunAsync(Automation automation, AutomationRun run, CancellationToken ct = default);
}

/// <summary>The names of Fleet's built-in automations.</summary>
public static class BuiltinAutomations
{
    public const string Dreaming = "dreaming";
}
