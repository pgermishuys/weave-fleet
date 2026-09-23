using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// Workflow runs and their step visits. Lists are the current user's; the runner reads by id as the run's owner, and
/// reads every user's unfinished runs once at start-up, the way the automation scheduler spans users.
/// </summary>
public sealed class WorkflowRunRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : IWorkflowRunRepository
{
    public async Task InsertAsync(WorkflowRun run)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO workflow_runs (
                id, user_id, workflow_id, workflow_name, definition, request, slug, title, repository_path, base_branch,
                branch, worktree_path, harness_type, harness_profile_id, options, status, current_step_id,
                waiting_reason, waiting_kind, result, created_at, updated_at, ended_at
            ) VALUES (
                @Id, @UserId, @WorkflowId, @WorkflowName, @Definition, @Request, @Slug, @Title, @RepositoryPath, @BaseBranch,
                @Branch, @WorktreePath, @HarnessType, @HarnessProfileId, @Options, @Status, @CurrentStepId,
                @WaitingReason, @WaitingKind, @Result, @CreatedAt, @UpdatedAt, @EndedAt
            )
            """,
            cmd => AddRunParameters(cmd, run));
    }

    public async Task UpdateAsync(WorkflowRun run)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE workflow_runs SET
                title = @Title, branch = @Branch, worktree_path = @WorktreePath, options = @Options, status = @Status,
                current_step_id = @CurrentStepId, waiting_reason = @WaitingReason, waiting_kind = @WaitingKind, result = @Result,
                updated_at = @UpdatedAt, ended_at = @EndedAt
            WHERE id = @Id
            """,
            cmd => AddRunParameters(cmd, run));
    }

    public async Task<WorkflowRun?> GetAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM workflow_runs WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            MapRun);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(int limit, string? workflowId = null)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT * FROM workflow_runs
            WHERE user_id = @UserId AND (@WorkflowId IS NULL OR workflow_id = @WorkflowId)
            ORDER BY created_at DESC, id DESC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("WorkflowId", workflowId);
                cmd.AddParameter("Limit", limit);
            },
            MapRun);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListUnfinishedAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM workflow_runs WHERE status IN ('running', 'waiting') ORDER BY created_at",
            MapRun);
    }

    public async Task InsertStepAsync(WorkflowRunStep visit)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO workflow_run_steps (
                id, run_id, step_id, visit, session_id, status, outcome, summary, note, finish, prompt_message_id,
                wrap_up_message_id, hand_off_note, files_checked, files_commit, files_commit_error, started_at, finished_at
            ) VALUES (
                @Id, @RunId, @StepId, @Visit, @SessionId, @Status, @Outcome, @Summary, @Note, @Finish, @PromptMessageId,
                @WrapUpMessageId, @HandOffNote, @FilesChecked, @FilesCommit, @FilesCommitError, @StartedAt, @FinishedAt
            )
            """,
            cmd => AddStepParameters(cmd, visit));
    }

    public async Task UpdateStepAsync(WorkflowRunStep visit)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE workflow_run_steps SET
                session_id = @SessionId, status = @Status, outcome = @Outcome, summary = @Summary, note = @Note,
                finish = @Finish, prompt_message_id = @PromptMessageId, wrap_up_message_id = @WrapUpMessageId,
                hand_off_note = @HandOffNote, files_checked = @FilesChecked, files_commit = @FilesCommit,
                files_commit_error = @FilesCommitError, finished_at = @FinishedAt
            WHERE id = @Id
            """,
            cmd => AddStepParameters(cmd, visit));
    }

    public async Task<IReadOnlyList<WorkflowRunStep>> ListStepsAsync(string runId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM workflow_run_steps WHERE run_id = @RunId ORDER BY started_at, visit, id",
            cmd => cmd.AddParameter("RunId", runId),
            MapStep);
    }

    public async Task<WorkflowRunStep?> GetStepBySessionAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM workflow_run_steps WHERE session_id = @SessionId ORDER BY started_at DESC LIMIT 1",
            cmd => cmd.AddParameter("SessionId", sessionId),
            MapStep);
    }

    private static void AddRunParameters(DbCommand cmd, WorkflowRun run)
    {
        cmd.AddParameter("Id", run.Id);
        cmd.AddParameter("UserId", run.UserId);
        cmd.AddParameter("WorkflowId", run.WorkflowId);
        cmd.AddParameter("WorkflowName", run.WorkflowName);
        cmd.AddParameter("Definition", run.Definition);
        cmd.AddParameter("Request", run.Request);
        cmd.AddParameter("Slug", run.Slug);
        cmd.AddParameter("Title", run.Title);
        cmd.AddParameter("RepositoryPath", run.RepositoryPath);
        cmd.AddParameter("BaseBranch", run.BaseBranch);
        cmd.AddParameter("Branch", run.Branch);
        cmd.AddParameter("WorktreePath", run.WorktreePath);
        cmd.AddParameter("HarnessType", run.HarnessType);
        cmd.AddParameter("HarnessProfileId", run.HarnessProfileId);
        cmd.AddParameter("Options", run.Options);
        cmd.AddParameter("Status", run.Status);
        cmd.AddParameter("CurrentStepId", run.CurrentStepId);
        cmd.AddParameter("WaitingReason", run.WaitingReason);
        cmd.AddParameter("WaitingKind", run.WaitingKind);
        cmd.AddParameter("Result", run.Result);
        cmd.AddParameter("CreatedAt", run.CreatedAt);
        cmd.AddParameter("UpdatedAt", run.UpdatedAt);
        cmd.AddParameter("EndedAt", run.EndedAt);
    }

    private static void AddStepParameters(DbCommand cmd, WorkflowRunStep step)
    {
        cmd.AddParameter("Id", step.Id);
        cmd.AddParameter("RunId", step.RunId);
        cmd.AddParameter("StepId", step.StepId);
        cmd.AddParameter("Visit", step.Visit);
        cmd.AddParameter("SessionId", step.SessionId);
        cmd.AddParameter("Status", step.Status);
        cmd.AddParameter("Outcome", step.Outcome);
        cmd.AddParameter("Summary", step.Summary);
        cmd.AddParameter("Note", step.Note);
        cmd.AddParameter("Finish", step.Finish);
        cmd.AddParameter("PromptMessageId", step.PromptMessageId);
        cmd.AddParameter("WrapUpMessageId", step.WrapUpMessageId);
        cmd.AddParameter("HandOffNote", step.HandOffNote);
        cmd.AddParameter("FilesChecked", step.FilesChecked ? 1 : 0);
        cmd.AddParameter("FilesCommit", step.FilesCommit);
        cmd.AddParameter("FilesCommitError", step.FilesCommitError);
        cmd.AddParameter("StartedAt", step.StartedAt);
        cmd.AddParameter("FinishedAt", step.FinishedAt);
    }

    private static WorkflowRun MapRun(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        WorkflowId = r.GetString(r.GetOrdinal("workflow_id")),
        WorkflowName = r.GetString(r.GetOrdinal("workflow_name")),
        Definition = r.GetString(r.GetOrdinal("definition")),
        Request = r.GetString(r.GetOrdinal("request")),
        Slug = r.GetString(r.GetOrdinal("slug")),
        Title = r.GetString(r.GetOrdinal("title")),
        RepositoryPath = r.GetString(r.GetOrdinal("repository_path")),
        BaseBranch = r.GetNullableString(r.GetOrdinal("base_branch")),
        Branch = r.GetNullableString(r.GetOrdinal("branch")),
        WorktreePath = r.GetNullableString(r.GetOrdinal("worktree_path")),
        HarnessType = r.GetString(r.GetOrdinal("harness_type")),
        HarnessProfileId = r.GetNullableString(r.GetOrdinal("harness_profile_id")),
        Options = r.GetString(r.GetOrdinal("options")),
        Status = r.GetString(r.GetOrdinal("status")),
        CurrentStepId = r.GetNullableString(r.GetOrdinal("current_step_id")),
        WaitingReason = r.GetNullableString(r.GetOrdinal("waiting_reason")),
        WaitingKind = r.GetNullableString(r.GetOrdinal("waiting_kind")),
        Result = r.GetNullableString(r.GetOrdinal("result")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        EndedAt = r.GetNullableString(r.GetOrdinal("ended_at")),
    };

    private static WorkflowRunStep MapStep(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        RunId = r.GetString(r.GetOrdinal("run_id")),
        StepId = r.GetString(r.GetOrdinal("step_id")),
        Visit = (int)r.GetInt64(r.GetOrdinal("visit")),
        SessionId = r.GetNullableString(r.GetOrdinal("session_id")),
        Status = r.GetString(r.GetOrdinal("status")),
        Outcome = r.GetNullableString(r.GetOrdinal("outcome")),
        Summary = r.GetNullableString(r.GetOrdinal("summary")),
        Note = r.GetNullableString(r.GetOrdinal("note")),
        Finish = r.GetNullableString(r.GetOrdinal("finish")),
        PromptMessageId = r.GetNullableString(r.GetOrdinal("prompt_message_id")),
        WrapUpMessageId = r.GetNullableString(r.GetOrdinal("wrap_up_message_id")),
        HandOffNote = r.GetNullableString(r.GetOrdinal("hand_off_note")),
        FilesChecked = r.GetInt64(r.GetOrdinal("files_checked")) != 0,
        FilesCommit = r.GetNullableString(r.GetOrdinal("files_commit")),
        FilesCommitError = r.GetNullableString(r.GetOrdinal("files_commit_error")),
        StartedAt = r.GetString(r.GetOrdinal("started_at")),
        FinishedAt = r.GetNullableString(r.GetOrdinal("finished_at")),
    };
}
