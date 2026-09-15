using System.Text.Json;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>A new automation's starting point, taken from a session: its first message and where it ran.</summary>
/// <param name="Prompt">The session's first message, as the person typed it.</param>
/// <param name="Folder">The folder it ran in: the repository for a worktree session; null for a quick chat.</param>
/// <param name="Isolation">"worktree" when the session had its own worktree, otherwise "existing".</param>
public sealed record AutomationDraft(string Prompt, string? Folder, string Isolation);

/// <summary>"Repeat on a schedule…": the session's own words and folder, so nothing is rewritten and no model is asked.</summary>
public sealed class AutomationDraftService(
    ISessionRepository sessionRepository,
    IWorkspaceRepository workspaceRepository,
    IMessageRepository messageRepository)
{
    public async Task<Result<AutomationDraft>> FromSessionAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor("Session", sessionId);

        var message = await messageRepository.GetFirstUserMessageAsync(sessionId);
        var prompt = message is null ? string.Empty : WithoutEventContext(TextOf(message.PartsJson));

        var workspace = await workspaceRepository.GetByIdAsync(session.WorkspaceId);
        if (workspace is null || workspace.SourceProviderId == SessionSourceProviderIds.QuickChat)
            return new AutomationDraft(prompt, null, "existing");

        var isWorktree = workspace.IsolationStrategy == "worktree";
        var folder = isWorktree ? workspace.SourceDirectory ?? workspace.Directory : workspace.Directory;
        return new AutomationDraft(prompt, folder, isWorktree ? "worktree" : "existing");
    }

    /// <summary>An event-triggered run's message is "[Context]…[Instruction]…"; the instruction is what was asked.</summary>
    private static string WithoutEventContext(string text)
    {
        const string instruction = "\n\n[Instruction]\n";
        var at = text.IndexOf(instruction, StringComparison.Ordinal);
        return text.StartsWith("[Context]\n", StringComparison.Ordinal) && at >= 0
            ? text[(at + instruction.Length)..].Trim()
            : text;
    }

    private static string TextOf(string partsJson)
    {
        try
        {
            var parts = JsonSerializer.Deserialize(partsJson, ApplicationJsonContext.Default.ListMessagePart) ?? [];
            return string.Join("\n\n", parts.OfType<TextPart>().Select(part => part.Text.Trim()).Where(text => text.Length > 0));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
