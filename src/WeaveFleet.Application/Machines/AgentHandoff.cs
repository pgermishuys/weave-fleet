using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Agents handing work to another machine: an agent in a session here starts a session on a machine in this Fleet's
/// list, gives it the task, and messages and reads it there. This Fleet makes every call with the token it keeps for
/// that machine, so the agent never sees a token, and only on machines the owner allowed (<c>AgentsAllowed</c>).
/// Experimental, off unless turned on, and only with messages between sessions on: it uses the same tools.
/// </summary>
public static class AgentHandoff
{
    /// <summary>The user preference that turns it on or off; when unset, <see cref="HarnessOptions.AgentHandoff"/> decides.</summary>
    public const string PreferenceKey = "AgentHandoff";

    /// <summary>Set to <c>1</c> in a harness process's environment when it was started with hand-offs on.</summary>
    public const string EnvironmentVariable = "FLEET_AGENT_HANDOFF";
}

/// <summary>Whether agents may hand work to other machines for the current user: its own switch and messages between sessions both on.</summary>
public sealed class AgentHandoffFeature(FleetOptions options, IUserPreferenceRepository preferences, SessionMessagesFeature sessionMessages)
{
    public async Task<bool> IsEnabledAsync()
    {
        var value = await preferences.GetAsync(AgentHandoff.PreferenceKey).ConfigureAwait(false);
        var on = string.IsNullOrWhiteSpace(value)
            ? options.Harness.AgentHandoff
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        return on && await sessionMessages.IsEnabledAsync().ConfigureAwait(false);
    }
}
