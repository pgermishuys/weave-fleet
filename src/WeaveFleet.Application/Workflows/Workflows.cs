using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// Workflows: Fleet runs a short list of steps, one session per agent step, and moves between them when a step's
/// agent calls <see cref="StepTool"/>. Experimental, and off unless turned on.
/// </summary>
public static class FleetWorkflows
{
    /// <summary>The user preference that turns it on or off; when unset, <see cref="HarnessOptions.Workflows"/> decides.</summary>
    public const string PreferenceKey = "Workflows";

    /// <summary>Set to <c>1</c> in a harness process's environment when it was started with workflows on.</summary>
    public const string EnvironmentVariable = "FLEET_WORKFLOWS";

    /// <summary>The tool a step's agent calls once, as its last action, to finish the step.</summary>
    public const string StepTool = "fleet_step_done";

    public const string TurnedOffMessage = "Workflows are turned off in Fleet's Settings.";

    /// <summary>
    /// The only thing Fleet adds to a step's prompt: which outcomes it can finish with, and how. This wording is the
    /// one real models followed in the feasibility tests (12 of 12 called the tool once, last).
    /// </summary>
    public static string Footer(IReadOnlyList<string> outcomes)
        => $"This is one step of a Fleet workflow. When the step is finished, call {StepTool} once, as your last action, "
           + $"with outcome set to one of: {string.Join(", ", outcomes)}. Put what the next step needs in summary.";
}

/// <summary>Whether workflows are on for the current user.</summary>
public sealed class WorkflowsFeature(FleetOptions options, IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync()
    {
        var value = await preferences.GetAsync(FleetWorkflows.PreferenceKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value)
            ? options.Harness.Workflows
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A model and effort a role or a step uses. A null model is the harness's default model.</summary>
/// <param name="Model"><c>provider/model</c>, or null for the default model.</param>
public sealed record WorkflowModelChoice(string? Model, string? Effort)
{
    public static readonly WorkflowModelChoice Default = new(null, null);

    /// <summary>The provider and model ids, split at the first slash (model ids may have slashes of their own).</summary>
    public (string? ProviderId, string? ModelId) Split()
    {
        var slash = Model?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash > 0 && slash < Model!.Length - 1 ? (Model[..slash], Model[(slash + 1)..]) : (null, null);
    }
}

/// <summary>
/// Which of the user's models does each kind of work, per harness: Settings → Workflows → Model roles. Kept in Fleet
/// for the user, not in the repo, so a workflow file someone shares works on the reader's providers.
/// </summary>
public sealed class WorkflowModelRoles(IUserPreferenceRepository preferences)
{
    public const string PreferenceKey = "WorkflowModelRoles";

    /// <summary>The roles the user mapped on <paramref name="harnessType"/>; a role that's missing uses the default model.</summary>
    public async Task<IReadOnlyDictionary<string, WorkflowModelChoice>> GetAsync(string harnessType)
    {
        var all = Read(await preferences.GetAsync(PreferenceKey).ConfigureAwait(false));
        return all.TryGetValue(harnessType, out var roles) ? roles : new Dictionary<string, WorkflowModelChoice>();
    }

    internal static Dictionary<string, Dictionary<string, WorkflowModelChoice>> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.DictionaryStringDictionaryStringWorkflowModelChoice) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The model a step runs on: the run's override for its role, then a model the step pins, then the role's model in
    /// Settings, then the default model.
    /// </summary>
    public static WorkflowModelChoice Resolve(
        WorkflowAgentStep step,
        IReadOnlyDictionary<string, WorkflowModelChoice> roles,
        IReadOnlyDictionary<string, WorkflowModelChoice>? overrides)
    {
        if (!WorkflowRoles.IsRole(step.Model))
            return new WorkflowModelChoice(step.Model, step.Effort);

        var choice = overrides is not null && overrides.TryGetValue(step.Model, out var over) ? over
            : roles.TryGetValue(step.Model, out var mapped) ? mapped
            : WorkflowModelChoice.Default;
        return step.Effort is null ? choice : choice with { Effort = step.Effort };
    }
}

/// <summary>What a run was started with, kept as JSON on the run.</summary>
public sealed record WorkflowRunOptions
{
    /// <summary>The optional steps switched on for this run.</summary>
    public List<string> OptionalSteps { get; init; } = [];

    /// <summary>The run's own model per role, from the Run box's Models menu.</summary>
    public Dictionary<string, WorkflowModelChoice> RoleOverrides { get; init; } = [];

    /// <summary>The model each agent step runs on, resolved when the run started.</summary>
    public Dictionary<string, WorkflowModelChoice> StepModels { get; init; } = [];

    /// <summary>
    /// "Check with me after each step": every agent step that starts while it's on is one the user finishes. A change
    /// applies from the next step; the running one keeps the way it started.
    /// </summary>
    public bool CheckWithMe { get; set; }

    public static WorkflowRunOptions Read(string json)
    {
        try
        {
            var options = JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.WorkflowRunOptions) ?? new WorkflowRunOptions();

            // Source generation leaves init defaults null when the JSON has no such property.
            return options with
            {
                OptionalSteps = options.OptionalSteps ?? [],
                RoleOverrides = options.RoleOverrides ?? [],
                StepModels = options.StepModels ?? [],
            };
        }
        catch (JsonException)
        {
            return new WorkflowRunOptions();
        }
    }

    public string Write() => JsonSerializer.Serialize(this, WorkflowJsonContext.Default.WorkflowRunOptions);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, WorkflowModelChoice>>))]
[JsonSerializable(typeof(WorkflowRunOptions))]
[JsonSerializable(typeof(WorkflowRunDto))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;
