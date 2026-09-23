using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Services.Worktrees;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>What the Run box sends to start a run.</summary>
/// <param name="Directory">The repository the run's worktree is made from.</param>
/// <param name="OptionalSteps">The optional steps switched on for this run.</param>
/// <param name="RoleOverrides">The run's own model per role, from the Models menu.</param>
/// <param name="CheckWithMe">"Check with me after each step", from the Run box: every agent step is one you finish.</param>
public sealed record StartWorkflowRunRequest(
    string WorkflowId,
    string Directory,
    string Request,
    string? BaseBranch = null,
    string? HarnessType = null,
    string? HarnessProfileId = null,
    IReadOnlyList<string>? OptionalSteps = null,
    IReadOnlyDictionary<string, WorkflowModelChoice>? RoleOverrides = null,
    bool CheckWithMe = false);

/// <summary>The library for a repository: the workflows Fleet can run there.</summary>
/// <param name="Repository">The repository's folder, or null when none was given or it isn't a repository.</param>
public sealed record WorkflowLibrary(string? Repository, string? RepositoryName, IReadOnlyList<WorkflowEntry> Workflows);

/// <summary>The workflows API: the library, starting runs, and answering them.</summary>
public sealed class WorkflowService(
    WorkflowRunner runner,
    IWorkflowRunRepository runs,
    RepositoryService repositories,
    IHarnessRegistry harnesses,
    HarnessCatalogService catalogs,
    WorkflowModelRoles roles,
    IBuiltInSkillCatalog builtInSkills,
    IUserPreferenceRepository preferences,
    IUserContext user,
    TimeProvider time)
{
    private const string DefaultHarnessPreferenceKey = "defaultHarnessType";
    private const string FallbackHarness = "opencode";
    private const int MaxRequestLength = 4000;

    public async Task<WorkflowLibraryDto> ListAsync(string? directory, CancellationToken ct)
    {
        var repository = await ResolveRepositoryAsync(directory, ct).ConfigureAwait(false);
        var entries = await WorkflowCatalog.ListAsync(repository.IsSuccess ? repository.Value.Path : null, ct).ConfigureAwait(false);
        return WorkflowLibraryDto.From(repository.IsSuccess
            ? new WorkflowLibrary(repository.Value.Path, repository.Value.Name, entries)
            : new WorkflowLibrary(null, null, entries));
    }

    public async Task<IReadOnlyList<WorkflowRunDto>> ListRunsAsync(string? workflowId, int limit)
    {
        var list = await runs.ListAsync(Math.Clamp(limit, 1, 200), workflowId).ConfigureAwait(false);
        var views = new List<WorkflowRunDto>(list.Count);
        foreach (var run in list)
            views.Add(await ViewAsync(run).ConfigureAwait(false));
        return views;
    }

    public async Task<Result<WorkflowRunDto>> GetRunAsync(string id)
    {
        var run = await runs.GetAsync(id).ConfigureAwait(false);
        return run is null ? FleetError.NotFoundFor("WorkflowRun", id) : await ViewAsync(run).ConfigureAwait(false);
    }

    public Task<Result<WorkflowRunDto>> AnswerAsync(string runId, string choiceId, string? note, bool? checkWithMe, CancellationToken ct)
        => runner.AnswerAsync(user.UserId, runId, choiceId, note, checkWithMe, ct);

    public Task<Result<WorkflowRunDto>> MoveOnAsync(string runId, string? outcome, string? note, CancellationToken ct)
        => runner.MoveOnAsync(user.UserId, runId, outcome, note, ct);

    public Task<Result<WorkflowRunDto>> SetCheckWithMeAsync(string runId, bool on, CancellationToken ct)
        => runner.SetCheckWithMeAsync(user.UserId, runId, on, ct);

    public Task<Result<WorkflowRunDto>> EndAsync(string runId, CancellationToken ct)
        => runner.EndAsync(user.UserId, runId, ct);

    /// <summary>
    /// Checks everything a run needs before any session starts, and says what's wrong in words the user can act on:
    /// the harness, the workflow file, and every enabled step's model and skill. Then starts the run.
    /// </summary>
    public async Task<Result<WorkflowRunDto>> StartAsync(StartWorkflowRunRequest request, CancellationToken ct)
    {
        var text = request.Request?.Trim();
        if (string.IsNullOrEmpty(text))
            return FleetError.ValidationError("Request", "Say what the run should do.");
        if (text.Length > MaxRequestLength)
            return FleetError.ValidationError("Request", $"Keep the request under {MaxRequestLength} characters; longer material can go in a file the steps read.");

        var repository = await ResolveRepositoryAsync(request.Directory, ct).ConfigureAwait(false);
        if (repository.IsFailure)
            return repository.Error;

        var harnessType = string.IsNullOrWhiteSpace(request.HarnessType)
            ? await preferences.GetAsync(DefaultHarnessPreferenceKey).ConfigureAwait(false) is { Length: > 0 } preferred ? preferred : FallbackHarness
            : request.HarnessType.Trim();
        if (harnesses.GetByType(harnessType) is not { } harness)
            return FleetError.NotFoundFor("Harness", harnessType);
        if (!harness.Capabilities.SupportsWorkflowSteps)
            return FleetError.ValidationError("HarnessType", NotAvailableOn(harness.DisplayName));

        var entry = await WorkflowCatalog.FindAsync(request.WorkflowId, repository.Value.Path, ct).ConfigureAwait(false);
        if (entry is null)
            return FleetError.NotFoundFor("Workflow", request.WorkflowId);
        if (entry.Definition is not { } workflow)
            return FleetError.ValidationError("Workflow", entry.Errors.Count > 0 ? entry.Errors[0].ToString() : "The workflow file doesn't read.");

        var optional = (request.OptionalSteps ?? [])
            .Where(id => workflow.Find(id) is WorkflowAgentStep { Optional: true })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var enabled = workflow.Steps.OfType<WorkflowAgentStep>().Where(step => !step.Optional || optional.Contains(step.Id)).ToList();

        var skillProblem = await CheckSkillsAsync(enabled).ConfigureAwait(false);
        if (skillProblem is not null)
            return FleetError.ValidationError("Skill", skillProblem);

        var overrides = (request.RoleOverrides ?? new Dictionary<string, WorkflowModelChoice>())
            .Where(pair => WorkflowRoles.IsRole(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var mapped = await roles.GetAsync(harnessType).ConfigureAwait(false);
        var models = enabled.ToDictionary(step => step.Id, step => WorkflowModelRoles.Resolve(step, mapped, overrides), StringComparer.Ordinal);

        var modelProblem = await CheckModelsAsync(harness, enabled, models, repository.Value.Path, request.HarnessProfileId, entry, ct).ConfigureAwait(false);
        if (modelProblem is not null)
            return FleetError.ValidationError("Model", modelProblem);

        var now = time.GetUtcNow().UtcDateTime.ToString("O");
        var slug = BranchSlug.From(text);
        var run = new WorkflowRun
        {
            Id = Ulid.NewUlid().ToString(),
            UserId = user.UserId,
            WorkflowId = entry.Id,
            WorkflowName = workflow.Name,
            Definition = entry.Text,
            Request = text,
            Slug = slug.Length > 0 ? slug : "change",
            Title = TitleFrom(text),
            RepositoryPath = repository.Value.Path,
            BaseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? null : request.BaseBranch.Trim(),
            HarnessType = harnessType,
            HarnessProfileId = string.IsNullOrWhiteSpace(request.HarnessProfileId) ? null : request.HarnessProfileId,
            Options = new WorkflowRunOptions { OptionalSteps = optional, RoleOverrides = overrides, StepModels = models, CheckWithMe = request.CheckWithMe }.Write(),
            Status = WorkflowRunStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await runner.StartAsync(run, ct).ConfigureAwait(false);
    }

    public static string NotAvailableOn(string harnessName)
        => $"Workflows aren't available on {harnessName}. Pick OpenCode or OpenCode 2.";

    /// <summary>A run's title: the request's first line, cut at a word.</summary>
    internal static string TitleFrom(string request)
    {
        const int max = 60;
        var line = request.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? request;
        if (line.Length <= max)
            return line.TrimEnd('.');

        var cut = line.LastIndexOf(' ', max);
        return line[..(cut > max / 2 ? cut : max)].TrimEnd() + "…";
    }

    /// <summary>A built-in skill a step uses has to be on; a skill of the user's own isn't Fleet's to check.</summary>
    private async Task<string?> CheckSkillsAsync(IEnumerable<WorkflowAgentStep> steps)
    {
        var shipped = builtInSkills.Skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        var on = await BuiltInSkillService.GetEnabledAsync(preferences).ConfigureAwait(false);
        var off = steps.FirstOrDefault(step => step.Skill is { } skill && shipped.Contains(skill) && !on.Contains(skill));
        return off is null ? null : $"{off.Title} uses {off.Skill}, which is off. Turn it on in Settings → Skills.";
    }

    /// <summary>Every model a step names must be one the harness offers here, or the run doesn't start.</summary>
    private async Task<string?> CheckModelsAsync(
        IHarness harness,
        IReadOnlyList<WorkflowAgentStep> steps,
        Dictionary<string, WorkflowModelChoice> models,
        string directory,
        string? profileId,
        WorkflowEntry entry,
        CancellationToken ct)
    {
        if (!steps.Any(step => models[step.Id].Model is not null))
            return null;

        var catalog = await catalogs.GetCatalogAsync(harness.Type, directory, ct, profileId).ConfigureAwait(false);
        if (catalog.IsFailure)
            return $"Fleet couldn't check the models on {harness.DisplayName}: {catalog.Error.Description}";
        if (catalog.Value is not { } offered)
            return null;

        foreach (var step in steps)
        {
            var choice = models[step.Id];
            var (providerId, modelId) = choice.Split();
            if (choice.Model is null)
                continue;

            var model = offered.Providers.FirstOrDefault(p => p.Id == providerId)?.Models.FirstOrDefault(m => m.Id == modelId);
            if (model is null)
            {
                var fix = WorkflowRoles.IsRole(step.Model)
                    ? $"Pick another model for {WorkflowRoles.DisplayName(step.Model)} in Settings → Workflows, or in the Run box's Models menu."
                    : $"Change {step.Title}'s model in {entry.File ?? "the workflow"}.";
                return $"{step.Title}'s model, {choice.Model}, isn't available on {harness.DisplayName}. {fix}";
            }

            if (choice.Effort is { } effort && model.Variants is { Count: > 0 } variants && !variants.Contains(effort))
                return $"{step.Title}'s effort, {effort}, isn't one {model.Name ?? model.Id} offers ({string.Join(", ", variants)}).";
        }

        return null;
    }

    private async Task<Result<RepositoryInfo>> ResolveRepositoryAsync(string? directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return FleetError.ValidationError("Directory", "Pick the repository the run works in.");

        var path = await repositories.ResolveRepositoryPathAsync(directory, ct).ConfigureAwait(false);
        if (path.IsFailure)
            return path.Error;

        var info = await repositories.GetRepositoryInfoAsync(path.Value, ct).ConfigureAwait(false);
        return info is null
            ? FleetError.ValidationError("Directory", "A workflow runs in a git repository; this folder isn't one.")
            : info;
    }

    private async Task<WorkflowRunDto> ViewAsync(WorkflowRun run)
    {
        var visits = await runs.ListStepsAsync(run.Id).ConfigureAwait(false);
        return WorkflowRunView.Build(run, WorkflowYaml.Parse(run.Definition, run.WorkflowId).Definition, visits);
    }
}

/// <summary>The library as the client shows it.</summary>
/// <param name="Repository">The repository whose <c>.weave/workflows</c> is listed, when there is one.</param>
public sealed record WorkflowLibraryDto(string? Repository, string? RepositoryName, IReadOnlyList<WorkflowDto> Workflows)
{
    public static WorkflowLibraryDto From(WorkflowLibrary library)
        => new(library.Repository, library.RepositoryName, library.Workflows.Select(WorkflowDto.From).ToList());
}

/// <summary>A workflow in the library: its steps, or the errors that keep its file from reading.</summary>
/// <param name="File">The repo-relative path; null for a built-in.</param>
/// <param name="Errors">Each with its file and line, e.g. ".weave/workflows/deps.yaml, line 12: …".</param>
public sealed record WorkflowDto(
    string Id,
    bool BuiltIn,
    string? File,
    string Name,
    string? Description,
    string? Placeholder,
    string? StartsFrom,
    string? RunsIn,
    IReadOnlyList<WorkflowStepDto> Steps,
    IReadOnlyList<string> Errors)
{
    public static WorkflowDto From(WorkflowEntry entry)
    {
        var workflow = entry.Definition;
        return new WorkflowDto(
            entry.Id,
            entry.IsBuiltIn,
            entry.File,
            workflow?.Name ?? entry.Name ?? Path.GetFileNameWithoutExtension(entry.File ?? entry.Id),
            workflow?.Description,
            workflow?.Placeholder,
            workflow?.StartsFrom,
            workflow?.RunsIn,
            workflow?.Steps.Select(WorkflowStepDto.From).ToList() ?? [],
            entry.Errors.Select(error => error.ToString()).ToList());
    }
}

/// <summary>A step as the library shows it.</summary>
/// <param name="Kind"><c>agent</c> or <c>you</c>.</param>
/// <param name="Model">A role or an exact <c>provider/model</c>; null for a You step.</param>
/// <param name="Routes">Outcome → the step it leads to; outcomes not here go to the next step.</param>
/// <param name="FinishYou"><c>finish: you</c>: you move the step on, not the agent.</param>
/// <param name="Writes">The files it declares, with their variables, e.g. <c>docs/design/{{slug}}.md</c>.</param>
public sealed record WorkflowStepDto(
    string Id,
    string Title,
    string Kind,
    string? Agent,
    string? Model,
    string? Effort,
    string? Skill,
    bool Optional,
    string? OptionalHint,
    IReadOnlyList<string> Outcomes,
    IReadOnlyDictionary<string, string> Routes,
    int? MaxLoops,
    string? Ask,
    IReadOnlyList<WorkflowChoice> Choices,
    bool FinishYou,
    IReadOnlyList<string> Writes)
{
    public static WorkflowStepDto From(WorkflowStep step) => step switch
    {
        WorkflowAgentStep agent => new WorkflowStepDto(
            agent.Id, agent.Title, "agent", agent.Agent, agent.Model, agent.Effort, agent.Skill, agent.Optional, agent.OptionalHint,
            agent.Outcomes, agent.Routes, agent.MaxLoops, null, [], agent.FinishYou, agent.Writes),
        WorkflowYouStep you => new WorkflowStepDto(
            you.Id, you.Title, "you", null, null, null, null, false, null, [], new Dictionary<string, string>(), null, you.Ask, you.Choices, false, []),
        _ => throw new InvalidOperationException($"Unknown step {step.GetType().Name}"),
    };
}
