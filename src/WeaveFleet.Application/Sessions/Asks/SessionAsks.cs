using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Asks;

/// <summary>
/// The user's replies to what the agent asked: a question's answers or its rejection, and a permission ask's answer
/// (once, always or reject). Each goes to the harness that asked.
/// </summary>
public sealed class SessionAsks(
    ISessionRepository sessionRepository,
    InstanceTracker instanceTracker,
    SessionActivation activation,
    ILogger<SessionAsks> logger)
{
    public async Task<Result<Unit>> AnswerQuestionAsync(
        string id,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            await instanceResult.Value.AnswerQuestionAsync(requestId, answers, ct);
        }
        catch (NotSupportedException ex)
        {
            return new FleetError("Session.QuestionNotSupported", ex.Message);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> RejectQuestionAsync(
        string id,
        string requestId,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            await instanceResult.Value.RejectQuestionAsync(requestId, ct);
        }
        catch (NotSupportedException ex)
        {
            return new FleetError("Session.QuestionNotSupported", ex.Message);
        }

        return Unit.Value;
    }

    /// <summary>
    /// Answers the agent's ask <paramref name="requestId"/> in session <paramref name="id"/>, the session whose harness
    /// asked. Only a running harness has asks: one that isn't running has nothing waiting.
    /// </summary>
    public async Task<Result<Unit>> ReplyToPermissionAsync(
        string id,
        string requestId,
        string reply,
        string? message,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        if (!PermissionReplies.IsAnswer(reply))
            return FleetError.ValidationError("Permission.Reply", "Answer once, always or reject.");

        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (instanceTracker.Get(sessionResult.Value.InstanceId) is not { } instance)
            return FleetError.NotFoundFor("PermissionRequest", requestId);

        try
        {
            await instance.ReplyToPermissionAsync(requestId, reply, message, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return FleetError.NotFoundFor("PermissionRequest", requestId);
        }

        return Unit.Value;
    }
}
