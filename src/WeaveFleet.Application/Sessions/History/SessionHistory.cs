using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.History;

/// <summary>
/// A session's messages, a page at a time, through <see cref="ISessionMessageProxy"/>: from the harness when it can
/// answer, from what Fleet saved otherwise, and an empty page when neither can.
/// </summary>
public sealed partial class SessionHistory(
    ISessionRepository sessionRepository,
    ISessionMessageProxy sessionMessageProxy,
    FleetOptions options,
    ILogger<SessionHistory> logger)
{
    public async Task<Result<MessagePage>> GetSessionMessagesAsync(
        string id,
        MessageQuery? query = null,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        // Validate session exists
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        return await GetPersistedMessagesAsync(id, query, ct);
    }

    private async Task<Result<MessagePage>> GetPersistedMessagesAsync(
        string sessionId,
        MessageQuery? query,
        CancellationToken ct)
    {
        var limit = query?.Limit ?? options.HistoryMessagePageSize;
        var before = query?.Before;

        try
        {
            // Delegate to the proxy, which will fetch from opencode if available,
            // or fall back to persisted messages if the harness is unavailable.
            return await sessionMessageProxy.GetMessagesAsync(sessionId, limit, before, ct);
        }
        catch (Exception ex)
        {
            LogProxyMessageFetchFailed(ex, sessionId);
            // Return empty result on failure (503-equivalent behavior)
            return Result.Success(new MessagePage([], false));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to fetch messages for session {SessionId} via proxy — returning empty result")]
    private partial void LogProxyMessageFetchFailed(Exception ex, string sessionId);
}
