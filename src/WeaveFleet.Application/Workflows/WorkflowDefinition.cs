using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Workflows;

/// <summary>A workflow as its file describes it: a short list of steps Fleet runs one after another.</summary>
/// <param name="Name">What the library calls it.</param>
/// <param name="Description">A sentence or two for the library.</param>
/// <param name="Placeholder">The Run box's hint, e.g. "What should it build?".</param>
/// <param name="StartsFrom">What a run starts from. Stage 1: <see cref="WorkflowStarts.Sentence"/> only.</param>
/// <param name="RunsIn">Where its steps work. Stage 1: <see cref="WorkflowPlaces.NewWorktree"/> only.</param>
public sealed record WorkflowDefinition(
    string Name,
    string? Description,
    string? Placeholder,
    string StartsFrom,
    string RunsIn,
    IReadOnlyList<WorkflowStep> Steps)
{
    public WorkflowStep? Find(string id) => Steps.FirstOrDefault(step => step.Id == id);

    public int IndexOf(string id)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Id == id)
                return i;
        }

        return -1;
    }
}

public static class WorkflowStarts
{
    public const string Sentence = "sentence";
}

public static class WorkflowPlaces
{
    public const string NewWorktree = "new-worktree";
}

/// <summary>Where an outcome or a choice leads: a step's id, or <see cref="End"/>.</summary>
public static class WorkflowTargets
{
    public const string End = "end";
}

/// <summary>Outcome names Fleet reads something into.</summary>
public static class WorkflowOutcomes
{
    /// <summary>
    /// The step couldn't do its job. When it ends the run, the run's result is the step's title and the first line of
    /// its summary, e.g. "Push and open the PR failed: there's no remote called origin."
    /// </summary>
    public const string Failed = "failed";
}

/// <summary>The roles a step's model can name; Settings → Workflows maps each to a model.</summary>
public static class WorkflowRoles
{
    public const string Strong = "strong";
    public const string Standard = "standard";
    public const string Fast = "fast";

    public static readonly IReadOnlyList<string> All = [Strong, Standard, Fast];

    public static bool IsRole(string? model) => model is Strong or Standard or Fast;

    public static string DisplayName(string role) => role switch
    {
        Strong => "Strong",
        Standard => "Standard",
        Fast => "Fast",
        _ => role,
    };
}

/// <summary>One step of a workflow.</summary>
/// <param name="Line">The step's line in its file, for errors.</param>
public abstract record WorkflowStep(string Id, string Title, int Line);

/// <summary>A new Fleet session with a prompt, an agent, a model and optionally a skill. It ends with an outcome.</summary>
/// <param name="Agent">The harness agent; null for the harness's default.</param>
/// <param name="Model">A role (<see cref="WorkflowRoles"/>), or an exact <c>provider/model</c>.</param>
/// <param name="Effort">The reasoning effort (the harness's variant); null for the role's or the model's default.</param>
/// <param name="Optional">Off unless switched on when a run starts.</param>
/// <param name="OptionalHint">When to switch it on, e.g. "For UI and new features".</param>
/// <param name="Routes">Outcome → the step it leads to, or <see cref="WorkflowTargets.End"/>. Outcomes not here go to the next step.</param>
/// <param name="MaxLoops">How many times this step may send work back to an earlier step in a run.</param>
/// <param name="Finish">
/// What the file says: <see cref="WorkflowFinishers.You"/> (the user and the agent work through the step together and
/// only the user ends it, with Move on; the step's session has no <c>fleet_step_done</c> and its prompt no footer),
/// <see cref="WorkflowFinishers.Agent"/> (the agent always ends it, even with Check with me on), or null when the file
/// doesn't say, so Check with me decides.
/// </param>
/// <param name="Writes">
/// The files the step declares (<c>writes:</c>), relative to the run's worktree, with variables still in them. Fleet
/// checks they exist before the next step starts, and the next step gets them as <c>{{previous.files}}</c>.
/// </param>
public sealed record WorkflowAgentStep(
    string Id,
    string Title,
    int Line,
    string? Agent,
    string Model,
    string? Effort,
    string? Skill,
    bool Optional,
    string? OptionalHint,
    string Prompt,
    IReadOnlyList<string> Outcomes,
    IReadOnlyDictionary<string, string> Routes,
    int? MaxLoops,
    string? Finish,
    IReadOnlyList<string> Writes) : WorkflowStep(Id, Title, Line)
{
    /// <summary>The file says <c>finish: you</c>.</summary>
    public bool FinishYou => Finish == WorkflowFinishers.You;

    /// <summary>The file says <c>finish: agent</c>: Check with me doesn't make it a step the user finishes.</summary>
    public bool FinishAgent => Finish == WorkflowFinishers.Agent;

    /// <summary>Whether a visit that starts now is one the user finishes: what the file says, else Check with me.</summary>
    public bool UserFinishes(bool checkWithMe) => Finish switch
    {
        WorkflowFinishers.You => true,
        WorkflowFinishers.Agent => false,
        _ => checkWithMe,
    };
}

/// <summary>The run stops and asks the user; each choice leads to a step or ends the run.</summary>
/// <param name="Ask">The question, e.g. "Build it this way?".</param>
public sealed record WorkflowYouStep(
    string Id,
    string Title,
    int Line,
    string Ask,
    IReadOnlyList<WorkflowChoice> Choices) : WorkflowStep(Id, Title, Line);

/// <summary>A choice at a You step.</summary>
/// <param name="To">A step's id, or <see cref="WorkflowTargets.End"/>.</param>
/// <param name="Note">The choice asks for a note, which goes into that step's next prompt.</param>
public sealed record WorkflowChoice(string Label, string To, bool Note);

/// <summary>Something wrong in a workflow file, at a line.</summary>
public sealed record WorkflowFileError(string File, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{File}, line {Line}: {Message}" : $"{File}: {Message}";
}
