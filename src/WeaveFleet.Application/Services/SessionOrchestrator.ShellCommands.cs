using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;

namespace WeaveFleet.Application.Services;

/// <summary>
/// A shell command the user runs from the composer (<c>!git status</c>). It runs in the session's folder with the
/// agent's own rights, so it's gated exactly as a prompt is: the session must be the caller's and not archived.
/// </summary>
public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Runs <paramref name="command"/> in the session's folder through its harness. Returns once the harness has taken
    /// it; the command and its output reach the conversation as the harness's events.
    /// </summary>
    public async Task<Result<Unit>> RunShellCommandAsync(string id, string? command, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        if (string.IsNullOrWhiteSpace(command))
            return FleetError.ValidationError("Session.ShellCommand", "Type a command to run.");
        if (command.Length > ShellCommands.MaxCommandLength)
            return FleetError.ValidationError("Session.ShellCommand", $"The command is longer than {ShellCommands.MaxCommandLength} characters.");

        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        if (harnessRegistry.GetByType(session.HarnessType)?.Capabilities.SupportsShellCommands != true)
        {
            return FleetError.ValidationError(
                "Session.ShellCommand",
                $"{HarnessDisplayName(session)} sessions can't run shell commands.");
        }

        var instanceResult = await GetOrActivateInstanceAsync(session, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            // The command's output arrives as events, so the subscription has to be up before it runs.
            await EnsureEventSubscriptionReadyAsync(instanceResult.Value, id, ct).ConfigureAwait(false);

            var choices = WithSessionChoices(options: null, session);
            await instanceResult.Value.RunShellCommandAsync(
                new ShellCommandOptions
                {
                    Command = command,
                    MessageId = AscendingMessageId.New(),
                    Agent = choices?.Agent,
                    ProviderId = choices?.ProviderId,
                    ModelId = choices?.ModelId,
                },
                ct).ConfigureAwait(false);
            return Unit.Value;
        }
        catch (HarnessBusyException ex)
        {
            return new FleetError("General.Conflict", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FleetError.ValidationError("Session.ShellCommand", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogShellCommandFailed(ex, id);
            return new FleetError("Session.ShellCommandFailed", "The command couldn't be run. Fleet's log has the details.");
        }
    }

    private string HarnessDisplayName(Session session)
        => harnessRegistry.GetByType(session.HarnessType)?.DisplayName ?? session.HarnessType;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to run a shell command in session {SessionId}")]
    private partial void LogShellCommandFailed(Exception ex, string sessionId);
}
