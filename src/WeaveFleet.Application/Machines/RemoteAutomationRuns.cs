using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Runs an automation on another machine in this Fleet's list (<see cref="Automation.TargetMachineId"/>): it starts the
/// run's session or workflow run through that machine's API with the token kept for it, sending what the composer's
/// Machine chip sends, so a machine on an older Fleet takes it too. The automation, its schedule and its runs stay here.
/// <para>
/// A machine that doesn't answer, or turns the token away, means the run is skipped and says why. Nothing is retried
/// and nothing moves to another machine.
/// </para>
/// </summary>
public sealed class RemoteAutomationRuns(
    RemoteMachineService machines,
    IHttpClientFactory httpClients,
    TimeProvider time)
{
    /// <summary>
    /// The client that starts runs: it gives up at once on a machine that isn't there, but waits for one that is while
    /// it makes the worktree and starts the harness, as the composer does.
    /// </summary>
    public const string HttpClientName = "RemoteAutomationRuns";

    /// <summary>Starts a session on the machine with the run's prompt, in the automation's folder there.</summary>
    public async Task<AutomationExecutionOutcome> StartSessionAsync(Automation automation, string prompt, CancellationToken ct)
    {
        var (machine, token, skipped) = await ResolveAsync(automation);
        if (machine is null)
            return skipped!;

        var (providerId, modelId) = AutomationExecutionService.SplitModel(automation.Model);
        var branch = IsWorktree(automation) ? AutomationSchedule.RunBranchName(automation, time.GetUtcNow()) : null;
        var body = new RemoteSessionStart
        {
            Directory = automation.WorkspaceId,
            Title = $"Automation: {automation.Name}",
            IsolationStrategy = branch is null ? "existing" : "worktree",
            Branch = branch,
            HarnessType = automation.HarnessType,
            InitialPrompt = prompt,
            Source = SourceFor(automation, branch),
            Agent = automation.Agent,
            Model = modelId is null ? null : new RemoteModel(providerId!, modelId),
        };

        var (answer, away, refused) = await RemoteMachineRequests.SendAsync(httpClients, HttpClientName, machine, token!, HttpMethod.Post, "/api/sessions", body, RemoteRunsJsonContext.Default.RemoteSessionStart, ct);
        if (answer is null)
            return away is not null ? SkippedOn(machine, away) : Failed(machine, refused!);

        using (answer)
        {
            var root = answer.RootElement;
            var sessionId = root.TryGetProperty("session", out var session) && session.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (sessionId is null)
                return Failed(machine, $"{machine.Name} didn't say which session it started.");

            var instanceId = root.TryGetProperty("instanceId", out var instance) ? instance.GetString() : null;
            return new AutomationExecutionOutcome(sessionId, instanceId, null, MachineId: machine.Id, MachineName: machine.Name);
        }
    }

    /// <summary>
    /// Prompts a session the automation started on the machine before. Null when the machine no longer has it or it's
    /// archived, so the caller starts a new one. The session answers with its own agent and model: through the API a
    /// prompt's choices would stay with the session, and the owner's pick there is theirs.
    /// </summary>
    public async Task<AutomationExecutionOutcome?> PromptSessionAsync(Automation automation, string sessionId, string prompt, CancellationToken ct)
    {
        var (machine, token, skipped) = await ResolveAsync(automation);
        if (machine is null)
            return skipped;

        var path = $"/api/sessions/{Uri.EscapeDataString(sessionId)}";
        var (session, away, _) = await RemoteMachineRequests.SendAsync<object>(httpClients, RemoteMachineService.HttpClientName, machine, token!, HttpMethod.Get, path, null, null, ct);
        if (session is null)
            return away is null ? null : SkippedOn(machine, away);

        using (session)
        {
            if (session.RootElement.TryGetProperty("retentionStatus", out var retention) && retention.GetString() == "archived")
                return null;
        }

        var (prompted, promptAway, _) = await RemoteMachineRequests.SendAsync(httpClients, HttpClientName, machine, token!, HttpMethod.Post, $"{path}/prompt", new RemotePrompt(prompt), RemoteRunsJsonContext.Default.RemotePrompt, ct);
        if (prompted is null)
            return promptAway is null ? null : SkippedOn(machine, promptAway);

        prompted.Dispose();
        return new AutomationExecutionOutcome(sessionId, null, null, MachineId: machine.Id, MachineName: machine.Name);
    }

    /// <summary>
    /// Starts the automation's workflow on the machine with the prompt as its request, Check with me off. A start the
    /// machine refuses is a skipped run with its reason, as it is here.
    /// </summary>
    public async Task<AutomationExecutionOutcome> StartWorkflowAsync(Automation automation, string request, CancellationToken ct)
    {
        var (machine, token, skipped) = await ResolveAsync(automation);
        if (machine is null)
            return skipped!;

        var body = new RemoteWorkflowStart(
            automation.WorkflowId ?? "",
            automation.WorkspaceId ?? "",
            request,
            automation.BaseBranch,
            automation.HarnessType,
            automation.WorkflowSteps,
            CheckWithMe: false);

        var (answer, away, refused) = await RemoteMachineRequests.SendAsync(httpClients, HttpClientName, machine, token!, HttpMethod.Post, "/api/workflows/runs", body, RemoteRunsJsonContext.Default.RemoteWorkflowStart, ct);
        if (answer is null)
            return SkippedOn(machine, away ?? refused!);

        using (answer)
        {
            var root = answer.RootElement;
            var runId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (runId is null)
                return Failed(machine, $"{machine.Name} didn't say which workflow run it started.");

            var firstSession = root.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array && sessions.GetArrayLength() > 0
                && sessions[0].TryGetProperty("sessionId", out var first)
                ? first.GetString()
                : null;
            return new AutomationExecutionOutcome(firstSession, null, null, runId, MachineId: machine.Id, MachineName: machine.Name);
        }
    }

    /// <summary>
    /// How a run's session on the machine is going: Running while it's busy, Done once it isn't or the machine no longer
    /// has it. Null when the machine didn't answer, which isn't Done.
    /// </summary>
    public async Task<string?> SessionStateAsync(string machineId, string sessionId, CancellationToken ct = default)
    {
        var (answered, body) = await GetOnMachineAsync(machineId, $"/api/sessions/{Uri.EscapeDataString(sessionId)}", ct);
        using (body)
        {
            if (!answered)
                return null;
            return body is not null && Text(body.RootElement, "activityStatus") == "busy" ? AutomationRunState.Running : AutomationRunState.Done;
        }
    }

    /// <summary>
    /// A workflow run on the machine: its status, and why a new firing shouldn't start while it's unfinished (in
    /// <see cref="AutomationWorkflows.UnfinishedReason"/>'s words). A run the machine no longer has reads as ended. Null
    /// when the machine didn't answer.
    /// </summary>
    public async Task<RemoteWorkflowRun?> GetWorkflowRunAsync(string machineId, string workflowRunId, CancellationToken ct = default)
    {
        var (answered, body) = await GetOnMachineAsync(machineId, $"/api/workflows/runs/{Uri.EscapeDataString(workflowRunId)}", ct);
        using (body)
        {
            if (!answered)
                return null;
            if (body is null || Text(body.RootElement, "status") is not { } status)
                return new RemoteWorkflowRun(WorkflowRunStatus.Ended, null);

            var root = body.RootElement;
            var currentStepId = Text(root, "currentStepId");
            var currentStep = root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray().FirstOrDefault(step => Text(step, "id") == currentStepId)
                : default;
            return new RemoteWorkflowRun(status, AutomationWorkflows.UnfinishedReason(
                status,
                root.TryGetProperty("waiting", out var waiting) ? Text(waiting, "stepTitle") : null,
                root.TryGetProperty("withYou", out var withYou) ? Text(withYou, "stepTitle") : null,
                currentStep.ValueKind == JsonValueKind.Object ? Text(currentStep, "title") : null));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads from the machine with the 5-second client. <c>Answered</c> is false when it didn't answer, turned the token
    /// away, or the watcher (<c>RemoteMachineWatcher</c>) last found it away, so a list of runs doesn't wait on a machine
    /// that's off. A 404 is an answer with no body: the machine no longer has it.
    /// </summary>
    private async Task<(bool Answered, JsonDocument? Body)> GetOnMachineAsync(string machineId, string path, CancellationToken ct)
    {
        var machine = await machines.GetAsync(machineId);
        var token = machine is null ? null : machines.TryTokenOf(machine);
        if (machine is null || token is null || machine.Status is RemoteMachineStatuses.Unreachable or RemoteMachineStatuses.Unauthorized)
            return (false, null);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{machine.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (true, null);
            if (!response.IsSuccessStatusCode)
                return (false, null);
            return (true, await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (false, null);
        }
    }

    /// <summary>The automation's machine and its token, or the skipped run that says why there's neither.</summary>
    private async Task<(RemoteMachine? Machine, string? Token, AutomationExecutionOutcome? Skipped)> ResolveAsync(Automation automation)
    {
        var machine = await machines.GetAsync(automation.TargetMachineId!);
        if (machine is null)
            return (null, null, AutomationExecutionOutcome.SkippedBecause("Skipped: the machine it runs on isn't in this Fleet's list any more."));

        var token = machines.TryTokenOf(machine);
        return token is null
            ? (null, null, SkippedOn(machine, $"can't read the token for {machine.Name}. Enter it again in Settings › Machines."))
            : (machine, token, null);
    }

    private static bool IsWorktree(Automation automation) =>
        automation.Isolation == "worktree" && !string.IsNullOrWhiteSpace(automation.WorkspaceId);

    /// <summary>
    /// Where the session starts on the machine, as the composer says it: a new worktree of the repository on
    /// <paramref name="branch"/>, the folder as it is, or a scratch folder when there's none.
    /// </summary>
    private static RemoteSource SourceFor(Automation automation, string? branch)
    {
        if (string.IsNullOrWhiteSpace(automation.WorkspaceId))
            return new RemoteSource(new RemoteSourceKey(SessionSourceProviderIds.QuickChat, SessionSourceTypeNames.QuickChat), new RemoteSourceInput());

        return branch is not null
            ? new RemoteSource(
                new RemoteSourceKey(SessionSourceProviderIds.Repository, SessionSourceTypeNames.Repository),
                new RemoteSourceInput
                {
                    RepositoryPath = automation.WorkspaceId,
                    IsolationStrategy = "worktree",
                    Branch = branch,
                    BaseBranch = automation.BaseBranch,
                })
            : new RemoteSource(
                new RemoteSourceKey(SessionSourceProviderIds.Local, SessionSourceTypeNames.Directory),
                new RemoteSourceInput { Directory = automation.WorkspaceId, IsolationStrategy = "existing" });
    }

    private static AutomationExecutionOutcome SkippedOn(RemoteMachine machine, string reason) =>
        new(null, null, $"Skipped: {reason}", Skipped: true, MachineId: machine.Id, MachineName: machine.Name);

    private static AutomationExecutionOutcome Failed(RemoteMachine machine, string error) =>
        new(null, null, $"Couldn't start: {error}", MachineId: machine.Id, MachineName: machine.Name);
}

/// <summary>A workflow run on another machine: its status and, while unfinished, why (<see cref="AutomationWorkflows.UnfinishedReason"/>).</summary>
public sealed record RemoteWorkflowRun(string Status, string? Unfinished);

internal sealed record RemoteSessionStart
{
    public string? Directory { get; init; }
    public string? Title { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
    public string? HarnessType { get; init; }
    public string? InitialPrompt { get; init; }
    public RemoteSource? Source { get; init; }
    public string? Agent { get; init; }
    public RemoteModel? Model { get; init; }
}

internal sealed record RemoteModel(
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("modelID")] string ModelId);

internal sealed record RemoteSource(RemoteSourceKey Key, RemoteSourceInput Input);

internal sealed record RemoteSourceKey(string ProviderId, string SourceType, string ActionId = SessionSourceActions.StartSession, int ContractVersion = 1);

internal sealed record RemoteSourceInput
{
    public string? RepositoryPath { get; init; }
    public string? Directory { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
    public string? BaseBranch { get; init; }
}

internal sealed record RemotePrompt(string Text);

internal sealed record RemoteWorkflowStart(
    string WorkflowId,
    string Directory,
    string Request,
    string? BaseBranch,
    string? HarnessType,
    IReadOnlyList<string> OptionalSteps,
    bool CheckWithMe);

[JsonSerializable(typeof(RemoteSessionStart))]
[JsonSerializable(typeof(RemotePrompt))]
[JsonSerializable(typeof(RemoteWorkflowStart))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class RemoteRunsJsonContext : JsonSerializerContext;
