using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// Starts a step as an ordinary Fleet session in the run's worktree: the first agent step makes the worktree from the
/// base branch, named by Settings → Worktree naming from the run's request, and every later step works in it.
/// </summary>
public sealed class WorkflowStepSessions(SessionOrchestrator orchestrator) : IWorkflowStepSessions
{
    public async Task<WorkflowStepSession> StartAsync(WorkflowRun run, WorkflowAgentStep agentStep, string prompt, WorkflowModelChoice model, CancellationToken ct)
    {
        var (providerId, modelId) = model.Split();
        var created = await orchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Title = $"{run.Title} · {agentStep.Title}",
            HarnessType = run.HarnessType,
            HarnessProfileId = run.HarnessProfileId,
            Source = RepositorySource(run),
            Agent = agentStep.Agent,
            ProviderId = providerId,
            ModelId = modelId,
            WorkflowRunId = run.Id,
            BranchNamingText = run.Request,
        }, ct).ConfigureAwait(false);
        if (created.IsFailure)
            return WorkflowStepSession.Failed(created.Error.Description);

        var session = created.Value.Session;
        var sent = await orchestrator.PromptSessionWithReceiptAsync(
                session.Id,
                prompt,
                new PromptOptions { Agent = agentStep.Agent, ProviderId = providerId, ModelId = modelId, Effort = model.Effort },
                userMessageId: null,
                correlationId: null,
                ct)
            .ConfigureAwait(false);

        return sent.IsFailure
            ? WorkflowStepSession.Failed(sent.Error.Description, session.Id)
            : new WorkflowStepSession(session.Id, session.Directory, created.Value.Branch, null);
    }

    /// <summary>The repository source: a new worktree for the run's first step, the run's worktree after that.</summary>
    private static SessionSourceSelection RepositorySource(WorkflowRun run)
    {
        // Written by hand rather than serialized, which keeps it trim-safe.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("repositoryPath", run.RepositoryPath);
            writer.WriteString("isolationStrategy", "worktree");
            if (run.WorktreePath is { } worktree)
                writer.WriteString("existingWorktreePath", worktree);
            else if (run.BaseBranch is { } baseBranch)
                writer.WriteString("baseBranch", baseBranch);
            writer.WriteEndObject();
        }

        return new SessionSourceSelection
        {
            Key = SessionSourceCatalog.RepositoryStartSession.Key,
            Input = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(),
        };
    }
}
