using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>
/// Drafting a workflow: the question, the parser's check of the answer, one follow-up with the errors, and nothing
/// written until Save. The conversation (a fork, or a throwaway session) is disposed on every path.
/// </summary>
public sealed class WorkflowDrafterTests : IDisposable
{
    private const string Valid = """
        ```yaml
        name: Fix a bug
        description: Finds a bug and fixes it.
        steps:
          - id: fix
            title: Fix
            model: standard
            prompt: |
              The request: {{request}}
              Fix it.
            outcomes: [done]
        ```
        """;

    // A loop back to the same step with no max.
    private const string LoopWithoutMax = """
        Here it is:

        ```yaml
        name: Fix a bug
        steps:
          - id: fix
            title: Fix
            model: standard
            prompt: |
              The request: {{request}}
            outcomes: [done, again]
            on: { again: fix }
        ```
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-drafts-").FullName;
    private readonly string _repo;
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly FakeSessionActivator _activator = new();
    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _runtime = new("opencode");
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly FakeOffTheRecordConversation _conversation = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly WorkflowDrafter _sut;

    public WorkflowDrafterTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "-q");

        var roots = new InMemoryWorkspaceRootRepository();
        roots.Seed(new WorkspaceRoot { Id = "root-1", Path = _root, CreatedAt = DateTime.UtcNow.ToString("O") });
        var user = new TestUserContext();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRootRepository>(roots);
        services.AddSingleton<IUserContext>(user);
        services.AddScoped(_ => new WorkspaceRootService(roots, user));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _registry.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsOffTheRecordPrompt = true, SupportsWorkflowSteps = true }));
        _registry.Register(new FakeHarness("claude-code", "Claude Code", new HarnessCapabilities()));
        _registry.Register(_runtime);

        _workspaces.Seed(new Workspace { Id = "ws-1", Directory = _repo });
        _sessions.Seed(new Session { Id = "s-1", WorkspaceId = "ws-1", Directory = _repo, Title = "Fix the login bug", HarnessType = "opencode" });
        _harness.OffTheRecordConversation = _conversation;
        _activator.ActivateBehavior = (_, _) => Task.FromResult(Result.Success<IHarnessSession>(_harness));

        _sut = new WorkflowDrafter(
            _sessions,
            _workspaces,
            _activator,
            new SessionActivityTracker(),
            _registry,
            new HarnessCatalogService(_registry, user, new FleetOptions(), NullLogger<HarnessCatalogService>.Instance),
            new WorkflowModelRoles(_preferences),
            new RepositoryService(scopes, NullLogger<RepositoryService>.Instance),
            user,
            NullLogger<WorkflowDrafter>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── The question ────────────────────────────────────────────────────────────

    [Fact]
    public void The_question_from_a_session_has_the_format_and_the_rules()
    {
        var question = WorkflowDraftPrompt.FromSession();

        question.ShouldContain("Describe the process this session followed as a Fleet workflow");
        question.ShouldContain("general terms, so the workflow is reusable: not a replay of this session");
        question.ShouldContain("Give each agent step a role as its model, never a provider/model");
        question.ShouldContain("strong for deciding and reviewing");
        question.ShouldContain("Add a You decide step wherever the user steered or approved in this session.");
        question.ShouldContain("Start every agent step's prompt with \"The request: {{request}}\"");
        question.ShouldContain("Answer with the file only, in one ```yaml block.");
        question.ShouldContain(WorkflowDraftPrompt.Format);
        question.ShouldNotContain("{{{");
    }

    [Fact]
    public void The_question_from_a_description_has_the_description_the_format_and_the_rules()
    {
        var question = WorkflowDraftPrompt.FromDescription("Bump the dependencies and check nothing broke.");

        question.ShouldStartWith("Write a Fleet workflow that does this:\n\nBump the dependencies and check nothing broke.");
        question.ShouldContain("general terms, so the workflow is reusable");
        question.ShouldContain("never a provider/model");
        question.ShouldContain("Add a You decide step wherever a person should approve before the work goes on.");
        question.ShouldContain(WorkflowDraftPrompt.Format);
    }

    [Fact]
    public void The_formats_example_is_a_valid_workflow_that_uses_roles_and_a_You_decide_step()
    {
        var example = WorkflowDraftPrompt.YamlIn(WorkflowDraftPrompt.Format);
        var parsed = WorkflowYaml.Parse(example, "the example");

        parsed.Errors.ShouldBeEmpty();
        parsed.Definition!.Steps.OfType<WorkflowAgentStep>().ShouldAllBe(step => WorkflowRoles.IsRole(step.Model));
        parsed.Definition.Steps.OfType<WorkflowYouStep>().ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("```yaml\nname: A\n```", "name: A\n")]
    [InlineData("Sure.\n\n```yml\nname: A\n```\nDone.", "name: A\n")]
    [InlineData("```\nname: A\n```", "name: A\n")]
    [InlineData("```text\nnotes\n```\n```yaml\nname: A\n```", "name: A\n")]
    [InlineData("name: A", "name: A\n")]
    [InlineData("```yaml\nname: A\nsteps:", "name: A\nsteps:\n")]
    public void The_file_is_the_answers_yaml_block(string answer, string file)
        => WorkflowDraftPrompt.YamlIn(answer).ShouldBe(file);

    // ── Reply → check → one retry ───────────────────────────────────────────────

    [Fact]
    public async Task A_valid_answer_is_the_draft_after_one_question()
    {
        _conversation.Answer(Valid, new OffTheRecordTokens(3400, 3100));

        var drafted = (await _sut.FromSessionAsync("s-1", CancellationToken.None)).ShouldBeSuccess();

        drafted.Check.IsValid.ShouldBeTrue();
        drafted.Check.Draft!.Name.ShouldBe("Fix a bug");
        drafted.Check.Text.ShouldStartWith("name: Fix a bug\n");
        (drafted.Asks, drafted.Tokens, drafted.SessionTitle, drafted.RepositoryName).ShouldBe((1, new OffTheRecordTokens(3400, 3100), "Fix the login bug", "repo"));
        _conversation.Prompts.ShouldBe([WorkflowDraftPrompt.FromSession()]);
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task An_answer_with_errors_is_asked_about_once_more_in_the_same_conversation_with_the_errors()
    {
        _conversation.Answer(LoopWithoutMax, new OffTheRecordTokens(3400, 3100)).Answer(Valid, new OffTheRecordTokens(3900, 3700));

        var drafted = (await _sut.FromSessionAsync("s-1", CancellationToken.None)).ShouldBeSuccess();

        drafted.Check.IsValid.ShouldBeTrue();
        (drafted.Asks, drafted.Tokens).ShouldBe((2, new OffTheRecordTokens(7300, 6800)));
        _conversation.Prompts.Count.ShouldBe(2);
        var retry = _conversation.Prompts[1];
        retry.ShouldStartWith("That file has errors:\n- line ");
        retry.ShouldContain("max");
        retry.ShouldEndWith("Answer with the whole file again, corrected, in one ```yaml block.");
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_answer_with_errors_is_the_draft_with_its_errors_and_no_third_question()
    {
        _conversation.Answer(LoopWithoutMax).Answer(LoopWithoutMax);

        var drafted = (await _sut.FromSessionAsync("s-1", CancellationToken.None)).ShouldBeSuccess();

        drafted.Check.IsValid.ShouldBeFalse();
        drafted.Check.Errors.ShouldNotBeEmpty();
        (drafted.Asks, drafted.Tokens).ShouldBe((2, null));
        _conversation.Prompts.Count.ShouldBe(2);
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task An_answer_that_isnt_YAML_is_the_draft_with_the_parsers_error_after_the_retry()
    {
        _conversation.Answer("I can't do that.").Answer("Still no.");

        var drafted = (await _sut.FromSessionAsync("s-1", CancellationToken.None)).ShouldBeSuccess();

        drafted.Check.IsValid.ShouldBeFalse();
        drafted.Check.Text.ShouldBe("Still no.\n");
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task No_answer_is_an_error_and_the_conversation_is_still_disposed()
    {
        _conversation.Answer((string?)null);

        var result = await _sut.FromSessionAsync("s-1", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("The model didn't answer with a workflow. Try again.");
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task A_harness_that_fails_is_an_error_and_the_conversation_is_still_disposed()
    {
        _conversation.Answer((_, _) => throw new HttpRequestException("connection refused"));

        var result = await _sut.FromSessionAsync("s-1", CancellationToken.None);

        result.Error.Description.ShouldBe("The harness couldn't ask the model: connection refused");
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task A_question_that_runs_out_of_time_is_an_error_and_the_conversation_is_still_disposed()
    {
        _conversation.Answer((_, _) => throw new TaskCanceledException("timed out"));

        var result = await _sut.FromSessionAsync("s-1", CancellationToken.None);

        result.Error.Description.ShouldBe("The model took too long to answer. Try again.");
        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_disposes_the_conversation()
    {
        using var cts = new CancellationTokenSource();
        _conversation.Answer(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null;
        });

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.FromSessionAsync("s-1", cts.Token));

        _conversation.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_is_written_until_the_draft_is_saved()
    {
        _conversation.Answer(Valid);

        var drafted = (await _sut.FromSessionAsync("s-1", CancellationToken.None)).ShouldBeSuccess();

        Directory.Exists(Path.Combine(_repo, ".weave")).ShouldBeFalse();
        Git("status", "--porcelain").ShouldBeEmpty();

        var saved = (await WorkflowRepoFiles.CreateAsync(_repo, drafted.Check.Text, await WorkflowCatalog.ListAsync(_repo), CancellationToken.None)).ShouldBeSuccess();
        saved.WorkflowId.ShouldBe("repo:fix-a-bug");
        File.ReadAllText(Path.Combine(_repo, ".weave", "workflows", "fix-a-bug.yaml")).ShouldBe(drafted.Check.Text);
    }

    // ── Where it can't ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_session_on_a_harness_that_cant_ask_off_the_record_is_refused_before_anything_is_asked()
    {
        _sessions.Seed(new Session { Id = "s-cc", WorkspaceId = "ws-1", Directory = _repo, Title = "Claude", HarnessType = "claude-code" });

        var result = await _sut.FromSessionAsync("s-cc", CancellationToken.None);

        result.Error.Description.ShouldBe("Save as workflow isn't available on Claude Code: it can't ask a question off the record.");
        _activator.ActivateAsyncCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_with_nothing_to_draft_from_says_so()
    {
        _harness.OffTheRecordConversation = null;

        var result = await _sut.FromSessionAsync("s-1", CancellationToken.None);

        result.Error.Description.ShouldBe("This session has nothing to draft from yet: send it a prompt first.");
    }

    [Fact]
    public async Task A_session_outside_a_git_repository_is_refused()
    {
        var folder = Path.Combine(_root, "notes");
        Directory.CreateDirectory(folder);
        _workspaces.Seed(new Workspace { Id = "ws-2", Directory = folder });
        _sessions.Seed(new Session { Id = "s-2", WorkspaceId = "ws-2", Directory = folder, HarnessType = "opencode" });

        var result = await _sut.FromSessionAsync("s-2", CancellationToken.None);

        result.Error.Description.ShouldBe("Path is not a git repository.");
    }

    [Fact]
    public async Task A_session_in_a_worktree_drafts_into_the_repository_it_came_from()
    {
        var worktree = Path.Combine(_root, "worktree");
        _workspaces.Seed(new Workspace { Id = "ws-wt", Directory = worktree, SourceDirectory = _repo });
        _sessions.Seed(new Session { Id = "s-wt", WorkspaceId = "ws-wt", Directory = worktree, HarnessType = "opencode" });
        _conversation.Answer(Valid);

        var drafted = (await _sut.FromSessionAsync("s-wt", CancellationToken.None)).ShouldBeSuccess();

        drafted.Repository.ShouldBe(WorkspaceRootService.CanonicalizePath(_repo));
    }

    // ── From a description ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_description_is_asked_with_no_session_on_the_Standard_roles_model_for_the_chosen_harness()
    {
        _preferences.Seed(WorkflowModelRoles.PreferenceKey, """{"opencode":{"standard":{"model":"fake/sonnet","effort":"high"},"strong":{"model":"fake/opus"}}}""");
        _runtime.OffTheRecordConversation = _conversation;
        _conversation.Answer(LoopWithoutMax).Answer(Valid);

        var drafted = (await _sut.FromDescriptionAsync(_repo, "Fix a bug.", "opencode", null, CancellationToken.None)).ShouldBeSuccess();

        (drafted.Asks, drafted.SessionTitle, drafted.Check.IsValid).ShouldBe((2, null, true));
        var asked = _runtime.OffTheRecordCalls.ShouldHaveSingleItem();
        (asked.ProviderId, asked.ModelId, asked.Variant, asked.Directory).ShouldBe(("fake", "sonnet", "high", WorkspaceRootService.CanonicalizePath(_repo)));
        _conversation.Prompts[0].ShouldBe(WorkflowDraftPrompt.FromDescription("Fix a bug."));
        _conversation.Disposals.ShouldBe(1);
        _activator.ActivateAsyncCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_description_with_no_Standard_role_uses_the_harnesss_default_model()
    {
        _runtime.OffTheRecordConversation = _conversation;
        _conversation.Answer(Valid);

        await _sut.FromDescriptionAsync(_repo, "Fix a bug.", "opencode", null, CancellationToken.None);

        var asked = _runtime.OffTheRecordCalls.ShouldHaveSingleItem();
        (asked.ProviderId, asked.ModelId, asked.Variant).ShouldBe((null, null, null));
    }

    [Fact]
    public async Task A_description_on_a_harness_workflows_dont_run_on_is_refused()
    {
        var result = await _sut.FromDescriptionAsync(_repo, "Fix a bug.", "claude-code", null, CancellationToken.None);

        result.Error.Description.ShouldBe("Workflows aren't available on Claude Code. Pick OpenCode or OpenCode 2.");
        _runtime.OffTheRecordCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_harness_that_cant_ask_without_a_session_says_so()
    {
        var result = await _sut.FromDescriptionAsync(_repo, "Fix a bug.", "opencode", null, CancellationToken.None);

        result.Error.Description.ShouldBe("OpenCode can't draft a workflow here. On OpenCode, turn on pooled mode in Settings.");
    }

    [Fact]
    public async Task An_empty_description_is_refused()
    {
        var result = await _sut.FromDescriptionAsync(_repo, "  ", "opencode", null, CancellationToken.None);

        result.Error.Description.ShouldBe("Say what the workflow should do.");
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

internal static class ResultAssertions
{
    public static T ShouldBeSuccess<T>(this Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        return result.Value;
    }
}
