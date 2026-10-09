using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>What every session service starts an operation with: the session, and a log scope naming it.</summary>
internal static class SessionLookup
{
    /// <summary>The session, or NotFound.</summary>
    public static async Task<Result<Session>> GetSessionAsync(this ISessionRepository sessions, string sessionId)
    {
        var session = await sessions.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        return session;
    }

    /// <summary>Tags the current trace with the session and opens a log scope for it.</summary>
    public static IDisposable? BeginSessionScope(this ILogger logger, string sessionId)
    {
        Activity.Current?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);
        return logger.BeginScope(new Dictionary<string, object> { [FleetInstrumentation.SessionIdTag] = sessionId });
    }
}
