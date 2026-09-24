using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>What the model drafted, checked by the parser the designer uses. Nothing has been written.</summary>
/// <param name="Repository">The repository the workflow would be saved in.</param>
/// <param name="SessionTitle">The session it was drafted from; null when it was drafted from a description.</param>
/// <param name="Asks">How many questions it took: 2 when the first answer had errors.</param>
/// <param name="Tokens">What the questions used, when the harness says.</param>
public sealed record WorkflowDrafted(
    string Repository,
    string RepositoryName,
    string? SessionTitle,
    WorkflowCheckResult Check,
    int Asks,
    OffTheRecordTokens? Tokens);

/// <summary>
/// Drafts a workflow file by asking the model, off the record: from a session (the process it followed) or from a
/// description. The answer is checked with the parser runs use; one with errors is asked about once more in the same
/// conversation, with the errors. Nothing is written: the draft opens in the designer, unsaved.
/// </summary>
public sealed partial class WorkflowDrafter(
    ISessionRepository sessions,
    IWorkspaceRepository workspaces,
    ISessionActivator activator,
    SessionActivityTracker activity,
    IHarnessRegistry harnesses,
    HarnessCatalogService catalogs,
    WorkflowModelRoles roles,
    RepositoryService repositories,
    IUserContext user,
    ILogger<WorkflowDrafter> logger)
{
    private const int MaxDescriptionLength = 4000;
    private const string DraftFile = "the draft";

    /// <summary>
    /// "Save as workflow…": the session's harness is asked about the session's own conversation, read from the
    /// provider's cache, and whatever it made for the question is deleted afterwards.
    /// </summary>
    public async Task<Result<WorkflowDrafted>> FromSessionAsync(string sessionId, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return FleetError.NotFoundFor("Session", sessionId);
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session", "Restore this session first: it's archived.");

        var harness = harnesses.GetByType(session.HarnessType);
        if (harness?.Capabilities.SupportsOffTheRecordPrompt != true)
            return FleetError.ValidationError("HarnessType", NotAvailableOn(harness?.DisplayName ?? session.HarnessType));

        if (SessionActivityTracker.IsInTurn(activity.GetEffectiveActivityStatus(sessionId)))
            return FleetError.ValidationError("Session", "Wait for this session's turn to end, then try again.");

        var workspace = await workspaces.GetByIdAsync(session.WorkspaceId).ConfigureAwait(false);
        var repository = await ResolveRepositoryAsync(workspace?.SourceDirectory ?? session.Directory, "this session's folder", ct).ConfigureAwait(false);
        if (repository.IsFailure)
            return repository.Error;

        // Unlike the recap, which never wakes a harness, this one's asked for: the user pressed the button.
        var instance = await activator.ActivateSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (instance.IsFailure)
            return instance.Error;

        var conversation = await instance.Value.StartOffTheRecordAsync(ct).ConfigureAwait(false);
        if (conversation is null)
            return FleetError.ValidationError("Session", "This session has nothing to draft from yet: send it a prompt first.");

        var title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title.Trim();
        return await DraftAsync(conversation, WorkflowDraftPrompt.FromSession(), repository.Value, title, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// New workflow → Describe it: one question with no session behind it, on the Standard role's model for
    /// <paramref name="harnessType"/>.
    /// </summary>
    public async Task<Result<WorkflowDrafted>> FromDescriptionAsync(
        string? directory, string? description, string? harnessType, string? profileId, CancellationToken ct)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
            return FleetError.ValidationError("Description", "Say what the workflow should do.");
        if (text.Length > MaxDescriptionLength)
            return FleetError.ValidationError("Description", $"Keep the description under {MaxDescriptionLength} characters.");

        var type = harnessType?.Trim() ?? string.Empty;
        if (harnesses.GetByType(type) is not { } harness)
            return FleetError.NotFoundFor("Harness", type);
        if (!harness.Capabilities.SupportsWorkflowSteps || harnesses.GetRuntimeByType(type) is not { } runtime)
            return FleetError.ValidationError("HarnessType", WorkflowService.NotAvailableOn(harness.DisplayName));

        if (string.IsNullOrWhiteSpace(directory))
            return FleetError.ValidationError("Directory", "Pick the repository the workflow goes in.");
        var repository = await ResolveRepositoryAsync(directory, "this folder", ct).ConfigureAwait(false);
        if (repository.IsFailure)
            return repository.Error;

        var profile = await catalogs.ResolveProfileAsync(type, profileId).ConfigureAwait(false);
        if (profile.IsFailure)
            return profile.Error;

        var standard = (await roles.GetAsync(type).ConfigureAwait(false)).GetValueOrDefault(WorkflowRoles.Standard) ?? WorkflowModelChoice.Default;
        var (providerId, modelId) = standard.Split();
        var conversation = await runtime.StartOffTheRecordAsync(
            new OffTheRecordOptions(user.UserId, repository.Value.Path, profile.Value, providerId, modelId, standard.Effort),
            ct).ConfigureAwait(false);
        if (conversation is null)
            return FleetError.ValidationError("HarnessType", $"{harness.DisplayName} can't draft a workflow here. On OpenCode, turn on pooled mode in Settings.");

        return await DraftAsync(conversation, WorkflowDraftPrompt.FromDescription(text), repository.Value, null, ct).ConfigureAwait(false);
    }

    public static string NotAvailableOn(string harnessName)
        => $"Save as workflow isn't available on {harnessName}: it can't ask a question off the record.";

    /// <summary>Asks, checks, and asks once more with the errors if there are any. The conversation is always disposed.</summary>
    internal async Task<Result<WorkflowDrafted>> DraftAsync(
        IOffTheRecordConversation conversation, string question, RepositoryInfo repository, string? sessionTitle, CancellationToken ct)
    {
        await using (conversation.ConfigureAwait(false))
        {
            try
            {
                var first = await conversation.AskAsync(question, ct).ConfigureAwait(false);
                if (first is null)
                    return NoAnswer();

                var check = WorkflowCheck.Text(WorkflowDraftPrompt.YamlIn(first.Text), DraftFile);
                if (check.IsValid)
                    return new WorkflowDrafted(repository.Path, repository.Name, sessionTitle, check, 1, first.Tokens);

                var second = await conversation.AskAsync(WorkflowDraftPrompt.Retry(check.Errors), ct).ConfigureAwait(false);
                if (second is null)
                    return new WorkflowDrafted(repository.Path, repository.Name, sessionTitle, check, 2, first.Tokens);

                var again = WorkflowCheck.Text(WorkflowDraftPrompt.YamlIn(second.Text), DraftFile);
                return new WorkflowDrafted(repository.Path, repository.Name, sessionTitle, again, 2, OffTheRecordTokens.Add(first.Tokens, second.Tokens));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return FleetError.ValidationError("Model", "The model took too long to answer. Try again.");
            }
            catch (HttpRequestException ex)
            {
                LogAskFailed(ex);
                return FleetError.ValidationError("Model", $"The harness couldn't ask the model: {ex.Message}");
            }
        }
    }

    private static FleetError NoAnswer() => FleetError.ValidationError("Model", "The model didn't answer with a workflow. Try again.");

    private async Task<Result<RepositoryInfo>> ResolveRepositoryAsync(string directory, string what, CancellationToken ct)
    {
        var path = await repositories.ResolveRepositoryPathAsync(directory, ct).ConfigureAwait(false);
        if (path.IsFailure)
            return path.Error;

        var info = await repositories.GetRepositoryInfoAsync(path.Value, ct).ConfigureAwait(false);
        return info is null
            ? FleetError.ValidationError("Directory", $"A workflow lives in a git repository, and {what} isn't one.")
            : info;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask the model for a workflow draft")]
    private partial void LogAskFailed(Exception ex);
}

/// <summary>The questions that draft a workflow: a short description of the file, and the rules for a reusable one.</summary>
public static partial class WorkflowDraftPrompt
{
    /// <summary>An example with every kind of step, then one line per key. Kept short: every draft sends it.</summary>
    internal const string Format = """
        The format, by example:

        ```yaml
        name: Fix a flaky test
        description: Finds why a test fails now and then, fixes it, and checks the fix holds.
        placeholder: "Which test? e.g. OrderTests.Refund"
        steps:
          - id: plan
            title: Plan
            agent: plan
            model: strong
            prompt: |
              The request: {{request}}
              Find out why it fails and how to fix it. Put the plan in summary.
            outcomes: [planned]

          - id: approve
            title: Approve the plan
            you: Fix it this way?
            choices:
              Approve: fix
              Change the plan: { to: plan, note: true }
              Stop: end

          - id: fix
            title: Fix
            agent: build
            model: standard
            prompt: |
              The request: {{request}}
              Fix it as planned:

              {{steps.plan.summary}}
            outcomes: [done]

          - id: check
            title: Check it holds
            model: fast
            prompt: |
              The request: {{request}}
              Run the test 20 times and say whether it passed every time.
            outcomes: [pass, fails]
            on: { fails: fix, max: 2 }
        ```

        - An agent step has id (lowercase letters, digits and dashes, unique, not "end"), title, agent (build, or plan
          for a step that only reads and decides), model, prompt, and outcomes: the words the agent can finish with.
        - on says where an outcome leads: a step's id or end. Outcomes it doesn't list go to the next step. An outcome
          that goes back to an earlier step, or the same one, needs max: how many times it may.
        - A You decide step has id, title, you (the question) and choices: each label leads to a step's id or end, and
          { to: <id>, note: true } asks the user for a note.
        - Prompts may use {{request}}, {{previous.summary}} (the last agent step's summary) and {{steps.<id>.summary}}.
          No other variables.
        """;

    private const string Roles =
        "- Give each agent step a role as its model, never a provider/model: strong for deciding and reviewing, " +
        "standard for writing and running code, fast for short tasks with a clear answer.";

    private const string Request =
        "- Start every agent step's prompt with \"The request: {{request}}\". Each step is a new session that knows only " +
        "its own prompt.";

    private const string AnswerWithFile = "- Answer with the file only, in one ```yaml block.";

    public static string FromSession() => $$$"""
        Describe the process this session followed as a Fleet workflow, so it can be run again for a similar request.
        Fleet runs a workflow's steps in order.

        Rules:
        - Write each step in general terms, so the workflow is reusable: not a replay of this session. Don't name files,
          values or decisions only this session had; what changes from one run to the next comes from {{request}}.
        {{{Request}}}
        {{{Roles}}}
        - Add a You decide step wherever the user steered or approved in this session.
        {{{AnswerWithFile}}}

        {{{Format}}}
        """;

    public static string FromDescription(string description) => $$$"""
        Write a Fleet workflow that does this:

        {{{description}}}

        Fleet runs a workflow's steps in order.

        Rules:
        - Write each step in general terms, so the workflow is reusable: what changes from one run to the next comes
          from {{request}}.
        {{{Request}}}
        {{{Roles}}}
        - Add a You decide step wherever a person should approve before the work goes on.
        {{{AnswerWithFile}}}

        {{{Format}}}
        """;

    /// <summary>The follow-up to an answer that has errors: each with its line, as the designer shows them.</summary>
    public static string Retry(IReadOnlyList<WorkflowProblem> errors)
    {
        var text = new StringBuilder("That file has errors:\n");
        foreach (var error in errors)
            text.Append("- ").Append(error.Line > 0 ? $"line {error.Line}: " : string.Empty).Append(error.Message).Append('\n');
        return text.Append("\nAnswer with the whole file again, corrected, in one ```yaml block.").ToString();
    }

    /// <summary>The file in an answer: its ```yaml block (or its first block), else the whole answer.</summary>
    public static string YamlIn(string answer)
    {
        var blocks = Fence().Matches(answer);
        var block = blocks.FirstOrDefault(m => m.Groups["lang"].Value is "yaml" or "yml") ?? blocks.FirstOrDefault();
        if (block is not null)
            return block.Groups["body"].Value.TrimEnd() + "\n";

        // An answer cut off before its closing fence.
        var open = Open().Match(answer);
        return (open.Success ? answer[(open.Index + open.Length)..] : answer).Trim() + "\n";
    }

    [GeneratedRegex(@"```(?<lang>[A-Za-z]*)[^\n]*\n(?<body>.*?)^[ \t]*```", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"```[A-Za-z]*[^\n]*\n")]
    private static partial Regex Open();
}

/// <summary>"Save as workflow…" on a session.</summary>
public sealed record DraftWorkflowFromSessionRequest(string SessionId);

/// <summary>New workflow → Describe it: what it should do, the repository it goes in, and the harness that asks.</summary>
public sealed record DraftWorkflowFromDescriptionRequest(
    string Directory,
    string Description,
    string? HarnessType = null,
    string? HarnessProfileId = null);

/// <summary>A drafted workflow as the designer opens it: unsaved, with where it came from and what it cost.</summary>
/// <param name="Repository">The repository Save creates the file in.</param>
/// <param name="SessionTitle">The session it was drafted from; null when it was drafted from a description.</param>
/// <param name="Asks">1, or 2 when the first answer had errors and the model was asked again.</param>
/// <param name="Tokens">What the questions used; null when the harness doesn't say.</param>
public sealed record DraftedWorkflowDto(
    string Repository,
    string RepositoryName,
    string? SessionTitle,
    WorkflowCheckDto Check,
    int Asks,
    DraftTokensDto? Tokens)
{
    public static DraftedWorkflowDto From(WorkflowDrafted drafted) => new(
        drafted.Repository,
        drafted.RepositoryName,
        drafted.SessionTitle,
        WorkflowCheckDto.From(drafted.Check),
        drafted.Asks,
        drafted.Tokens is { } tokens ? new DraftTokensDto(tokens.Total, tokens.FromCache) : null);
}

/// <param name="FromCache">How many of <paramref name="Total"/> were read from the provider's cache.</param>
public sealed record DraftTokensDto(long Total, long FromCache);
