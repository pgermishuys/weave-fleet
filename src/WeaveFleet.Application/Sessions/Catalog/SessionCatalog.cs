using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Catalog;

/// <summary>What the session's harness offers the composer: its models, slash commands and agents. Asking wakes the session.</summary>
public sealed class SessionCatalog(
    ISessionRepository sessionRepository,
    SessionActivation activation,
    ILogger<SessionCatalog> logger)
{
    public async Task<Result<IReadOnlyList<ProviderInfo>>> GetSessionModelsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var providers = await instanceResult.Value.GetProvidersAsync(ct);
        return Result.Success(providers);
    }

    public async Task<Result<IReadOnlyList<CommandInfo>>> GetSessionCommandsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var commands = await instanceResult.Value.GetCommandsAsync(ct);
        return Result.Success(commands);
    }

    public async Task<Result<IReadOnlyList<AgentInfo>>> GetSessionAgentsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var agents = await instanceResult.Value.GetAgentsAsync(ct);
        return Result.Success(agents);
    }
}
