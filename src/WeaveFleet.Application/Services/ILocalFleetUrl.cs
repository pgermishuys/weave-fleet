namespace WeaveFleet.Application.Services;

/// <summary>
/// The URL a process on this machine uses to call back into Fleet, such as a harness plugin calling the
/// agent bridge: a loopback address with the port Fleet listens on.
/// </summary>
public interface ILocalFleetUrl
{
    /// <summary>The URL, or <c>null</c> while Fleet listens on a port it hasn't been given yet.</summary>
    string? TryGet();
}
