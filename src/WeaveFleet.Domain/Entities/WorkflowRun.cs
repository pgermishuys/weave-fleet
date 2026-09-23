namespace WeaveFleet.Domain.Entities;

/// <summary>
/// One run of a workflow. Fleet moves it from step to step itself; the run and its steps are rows, so a restart
/// picks it up where it was.
/// </summary>
public sealed class WorkflowRun
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    /// <summary>The workflow it runs: <c>builtin:…</c> or <c>repo:…</c>.</summary>
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    /// <summary>The workflow file as it was when the run started. Editing the file doesn't change a run in flight.</summary>
    public string Definition { get; set; } = string.Empty;
    /// <summary>What the user asked for, <c>{{request}}</c> in the steps' prompts.</summary>
    public string Request { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>The repository the run's worktree is made from.</summary>
    public string RepositoryPath { get; set; } = string.Empty;
    public string? BaseBranch { get; set; }
    /// <summary>The run's branch, once its worktree exists.</summary>
    public string? Branch { get; set; }
    /// <summary>The run's worktree, made by its first agent step; every later step works there too.</summary>
    public string? WorktreePath { get; set; }
    public string HarnessType { get; set; } = string.Empty;
    public string? HarnessProfileId { get; set; }
    /// <summary>JSON: the optional steps switched on, the run's model overrides, and each step's model.</summary>
    public string Options { get; set; } = "{}";
    /// <summary>See <see cref="WorkflowRunStatus"/>.</summary>
    public string Status { get; set; } = WorkflowRunStatus.Running;
    public string? CurrentStepId { get; set; }
    /// <summary>Why the run is waiting on the user, in words; null unless <see cref="Status"/> is waiting.</summary>
    public string? WaitingReason { get; set; }
    /// <summary>How the run ended, in words ("PR #12 opened", "Ended by you at Review").</summary>
    public string? Result { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? EndedAt { get; set; }
}

public static class WorkflowRunStatus
{
    public const string Running = "running";
    /// <summary>Stopped on the user: a You step, a step that stopped without an outcome, or a loop past its maximum.</summary>
    public const string Waiting = "waiting";
    public const string Done = "done";
    /// <summary>The user ended it. Its sessions stay as ordinary sessions.</summary>
    public const string Ended = "ended";
    /// <summary>Fleet couldn't start a step.</summary>
    public const string Failed = "failed";

    public static bool IsFinished(string status) => status is Done or Ended or Failed;
}

/// <summary>One visit to a step of a run. A loop back to a step is a new visit.</summary>
public sealed class WorkflowRunStep
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public int Visit { get; set; } = 1;
    /// <summary>The session an agent step runs in.</summary>
    public string? SessionId { get; set; }
    /// <summary>See <see cref="WorkflowRunStepStatus"/>.</summary>
    public string Status { get; set; } = WorkflowRunStepStatus.Running;
    /// <summary>The outcome the step finished with, or the choice made at a You step.</summary>
    public string? Outcome { get; set; }
    /// <summary>What the agent passed to <c>fleet_step_done</c>; the next step's <c>{{previous.summary}}</c>.</summary>
    public string? Summary { get; set; }
    /// <summary>A note the user sent back into this visit.</summary>
    public string? Note { get; set; }
    public string StartedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}

public static class WorkflowRunStepStatus
{
    public const string Running = "running";
    /// <summary>Waiting on the user: stopped without an outcome, past a loop's maximum, or a You step.</summary>
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Skipped = "skipped";
    /// <summary>A You step the user answered.</summary>
    public const string Decided = "decided";
}
