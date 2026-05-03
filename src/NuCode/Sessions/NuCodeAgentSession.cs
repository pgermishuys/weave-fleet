using Microsoft.Agents.AI;

namespace NuCode.Sessions;

/// <summary>
/// Bridges a <see cref="NuCodeSession"/> into the Microsoft Agent Framework's
/// <see cref="AgentSession"/> so that NuCode sessions can be passed to
/// <see cref="ChatClientAgent.InvokeAsync"/> calls.
/// </summary>
public sealed class NuCodeAgentSession : AgentSession
{
    private const string SessionKey = "nucode.session";
    private const string StatusKey = "nucode.status";

    private NuCodeSession _session;
    private SessionStatus _status;

    /// <summary>
    /// Creates a new agent session wrapping the given NuCode session.
    /// </summary>
    public NuCodeAgentSession(NuCodeSession session)
    {
        _session = session;
        _status = new IdleSessionStatus();
    }

    /// <summary>
    /// Gets or sets the underlying NuCode session.
    /// </summary>
    public NuCodeSession Session
    {
        get => _session;
        set => _session = value;
    }

    /// <summary>
    /// Gets or sets the current processing status.
    /// </summary>
    public SessionStatus Status
    {
        get => _status;
        set => _status = value;
    }
}
