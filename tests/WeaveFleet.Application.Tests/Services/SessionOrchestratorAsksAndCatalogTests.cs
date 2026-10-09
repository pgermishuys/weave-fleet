using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// The agent's asks (questions and permission asks), what the session's harness offers (models, commands, agents), and
/// the session's message history, through <see cref="SessionOrchestrator"/>.
/// </summary>
public sealed class SessionOrchestratorAsksAndCatalogTests : IAsyncDisposable
{
    private const string SessionId = "sess-asks";
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessRuntime _runtime;
    private readonly FakeHarnessSession _running = new("inst-asks");

    public SessionOrchestratorAsksAndCatalogTests()
    {
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            WorkspaceId = "ws-asks",
            InstanceId = "inst-asks",
            Title = "Rate limit headers",
            Status = "active",
            Directory = "/tmp/fleet-asks",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active",
            HarnessType = "opencode",
            RuntimeMode = "manual",
            HarnessResumeToken = "token-asks",
            UserId = "user-1",
        });
    }

    public ValueTask DisposeAsync() => _running.DisposeAsync();

    private SessionOrchestrator Build(bool running = true)
    {
        if (running)
            _builder.InstanceTracker.Register("inst-asks", _running);
        return _builder.Build();
    }

    [Fact]
    public async Task an_answer_goes_to_the_harness_that_asked()
    {
        var sut = Build();

        var result = await sut.AnswerQuestionAsync(SessionId, "que_1", [["Yes"], ["Staging", "Production"]]);

        result.IsSuccess.ShouldBeTrue();
        var (requestId, answers) = _running.AnsweredQuestions.ShouldHaveSingleItem();
        requestId.ShouldBe("que_1");
        answers.Select(a => string.Join(",", a)).ShouldBe(["Yes", "Staging,Production"]);
    }

    [Fact]
    public async Task a_rejected_question_goes_to_the_harness_that_asked()
    {
        var sut = Build();

        (await sut.RejectQuestionAsync(SessionId, "que_2")).IsSuccess.ShouldBeTrue();

        _running.RejectedQuestions.ShouldBe(["que_2"]);
    }

    [Fact]
    public async Task a_harness_without_questions_says_so()
    {
        _running.QuestionsNotSupported = true;
        var sut = Build();

        (await sut.AnswerQuestionAsync(SessionId, "que_1", [["Yes"]])).Error.Code.ShouldBe("Session.QuestionNotSupported");
        (await sut.RejectQuestionAsync(SessionId, "que_1")).Error.Code.ShouldBe("Session.QuestionNotSupported");
    }

    [Fact]
    public async Task a_question_the_harness_does_not_have_is_not_found()
    {
        _running.QuestionsUnknown = true;
        var sut = Build();

        (await sut.AnswerQuestionAsync(SessionId, "que_gone", [["Yes"]])).Error.Code.ShouldBe("QuestionRequest.NotFound");
        (await sut.RejectQuestionAsync(SessionId, "que_gone")).Error.Code.ShouldBe("QuestionRequest.NotFound");
    }

    [Fact]
    public async Task questions_on_a_missing_session_are_not_found()
    {
        var sut = Build();

        (await sut.AnswerQuestionAsync("sess-gone", "que_1", [["Yes"]])).Error.Code.ShouldEndWith(".NotFound");
        (await sut.RejectQuestionAsync("sess-gone", "que_1")).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task a_permission_reply_goes_to_the_harness_that_asked()
    {
        _running.PendingPermissions.Add("per_1");
        var sut = Build();

        var result = await sut.ReplyToPermissionAsync(SessionId, "per_1", PermissionReplies.Reject, "Not on main");

        result.IsSuccess.ShouldBeTrue();
        _running.PermissionReplies.ShouldHaveSingleItem().ShouldBe(("per_1", PermissionReplies.Reject, (string?)"Not on main"));
    }

    [Fact]
    public async Task a_permission_reply_that_is_not_an_answer_is_refused()
    {
        _running.PendingPermissions.Add("per_1");
        var sut = Build();

        var result = await sut.ReplyToPermissionAsync(SessionId, "per_1", "maybe", null);

        result.Error.Description.ShouldBe("Answer once, always or reject.");
        _running.PermissionReplies.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_permission_reply_to_an_ask_the_harness_does_not_have_is_not_found()
    {
        var sut = Build();

        (await sut.ReplyToPermissionAsync(SessionId, "per_none", PermissionReplies.Once, null)).Error.Code.ShouldBe("PermissionRequest.NotFound");
    }

    [Fact]
    public async Task a_permission_reply_to_a_session_that_is_not_running_is_not_found_and_does_not_wake_it()
    {
        var sut = Build(running: false);

        (await sut.ReplyToPermissionAsync(SessionId, "per_1", PermissionReplies.Once, null)).Error.Code.ShouldBe("PermissionRequest.NotFound");
        _runtime.ResumeCalls.ShouldBeEmpty();
        _runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_harness_lists_its_models_commands_and_agents()
    {
        _running.Providers = [new ProviderInfo { Id = "fakellm", Models = [] }];
        _running.Commands = [new CommandInfo { Name = "review" }];
        _running.Agents = [new AgentInfo { Name = "build" }, new AgentInfo { Name = "plan" }];
        var sut = Build();

        (await sut.GetSessionModelsAsync(SessionId)).Value.Select(p => p.Id).ShouldBe(["fakellm"]);
        (await sut.GetSessionCommandsAsync(SessionId)).Value.Select(c => c.Name).ShouldBe(["review"]);
        (await sut.GetSessionAgentsAsync(SessionId)).Value.Select(a => a.Name).ShouldBe(["build", "plan"]);
        (await sut.GetSessionModelsAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task the_history_comes_from_the_proxy_a_page_at_a_time()
    {
        var asked = new List<(string SessionId, int? Limit, string? Before)>();
        _builder.SessionMessageProxy.GetMessagesBehavior = (id, limit, before, _) =>
        {
            asked.Add((id, limit, before));
            return Task.FromResult(new MessagePage([], HasMore: true));
        };
        var sut = Build();

        var page = await sut.GetSessionMessagesAsync(SessionId, new MessageQuery(20, "msg_9"));
        await sut.GetSessionMessagesAsync(SessionId);

        page.Value.HasMore.ShouldBeTrue();
        asked.ShouldBe([(SessionId, 20, "msg_9"), (SessionId, new WeaveFleet.Application.Configuration.FleetOptions().HistoryMessagePageSize, (string?)null)]);
    }

    [Fact]
    public async Task history_that_cannot_be_read_is_an_empty_page_and_a_missing_session_is_not_found()
    {
        _builder.SessionMessageProxy.GetMessagesBehavior = (_, _, _, _) => throw new HttpRequestException("harness gone");
        var sut = Build();

        var page = await sut.GetSessionMessagesAsync(SessionId);

        page.Value.Messages.ShouldBeEmpty();
        page.Value.HasMore.ShouldBeFalse();
        (await sut.GetSessionMessagesAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
    }
}
