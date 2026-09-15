using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IAutomationRunRepository
{
    Task InsertAsync(AutomationRun run);
    /// <summary>Records how a run that was starting ended up: its session, or why it failed.</summary>
    Task CompleteAsync(string id, string status, string? sessionId, string? instanceId, string? reason);
    /// <summary>An automation's runs, newest first. Not scoped to a user: load the automation as its owner first.</summary>
    Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(string automationId, int limit);
    /// <summary>Each automation's newest run, keyed by automation id.</summary>
    Task<IReadOnlyDictionary<string, AutomationRun>> GetLatestPerAutomationAsync();
    /// <summary>The newest schedule occurrence recorded for each automation (UTC, ISO 8601), across all users.</summary>
    Task<IReadOnlyDictionary<string, string>> GetLastScheduledForAsync();
    /// <summary>Marks runs still "starting" from before <paramref name="beforeUtc"/> as failed, e.g. after a crash.</summary>
    Task<int> FailStaleStartingAsync(string beforeUtc, string reason);
}
