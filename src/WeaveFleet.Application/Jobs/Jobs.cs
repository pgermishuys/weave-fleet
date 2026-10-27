using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Jobs;

/// <summary>
/// Jobs: one queue of work that free seats on any of the user's machines pick up. This Fleet holds the queue and does
/// the matching, and starts each job as a session on the machine that takes it. Experimental, and off unless turned on.
/// </summary>
public static class FleetJobs
{
    /// <summary>The user preference that turns it on or off; when unset, <see cref="HarnessOptions.Jobs"/> decides.</summary>
    public const string PreferenceKey = "Jobs";

    public const string TurnedOffMessage = "Jobs are turned off in Fleet's Settings.";

    /// <summary>The seats this machine gives jobs until its settings say otherwise. Other machines take none.</summary>
    public const int DefaultLocalSeats = 2;
}

/// <summary>Whether jobs are on for the current user.</summary>
public sealed class JobsFeature(FleetOptions options, IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync()
    {
        var value = await preferences.GetAsync(FleetJobs.PreferenceKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value)
            ? options.Harness.Jobs
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
