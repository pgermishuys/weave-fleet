using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Skills;

/// <summary>
/// Improve: one question off the record with only what the user chose, on the session's harness, folder and model; an
/// answer that breaks the skill is asked about once more; nothing is saved; the conversation is always disposed.
/// </summary>
public sealed class SkillImproverTests : IDisposable
{
    private const string Name = "fleet-code-review";
    private const string Fleet = "---\nname: fleet-code-review\ndescription: Reviews.\n---\n\nReport what would hurt someone.\n\n```bash\ngit diff\n```\n";
    private const string Better = "---\nname: fleet-code-review\ndescription: Reviews.\n---\n\nReport what would hurt someone. Naming and style aren't findings.\n\n```bash\ngit diff\n```\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-improve-").FullName;
    private readonly InMemorySessionRepository _sessions = new();
    private readonly FakeSessionActivator _activator = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _runtime = new("opencode");
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly FakeOffTheRecordConversation _conversation = new();
    private readonly InMemorySkillVersionStore _versions = new();
    private readonly BuiltInSkillService _skills;
    private readonly SkillImprover _sut;

    public SkillImproverTests()
    {
        var user = new TestUserContext("owner-1");
        _registry.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsOffTheRecordPrompt = true }));
        _registry.Register(new FakeHarness("claude-code", "Claude Code", new HarnessCapabilities()));
        _registry.Register(_runtime);
        _runtime.OffTheRecordConversation = _conversation;
        _harness.OffTheRecordConversation = _conversation;
        _activator.ActivateBehavior = (_, _) => Task.FromResult(Result.Success<IHarnessSession>(_harness));

        _sessions.Seed(new Session
        {
            Id = "s-1",
            Title = "Review auth refactor",
            Directory = _folder,
            HarnessType = "opencode",
            SelectedProviderId = "anthropic",
            SelectedModelId = "claude-sonnet-5",
        });

        _skills = new BuiltInSkillService(new Catalog(), new InMemoryUserPreferenceRepository(), user: user, versions: _versions);
        _sut = new SkillImprover(
            _sessions,
            _activator,
            _activity,
            _registry,
            new HarnessCatalogService(_registry, user, new FleetOptions(), NullLogger<HarnessCatalogService>.Instance),
            _skills,
            user,
            NullLogger<SkillImprover>.Instance);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Asks_once_with_the_note_what_the_user_chose_and_the_skill_on_the_sessions_folder_and_model()
    {
        _conversation.Answer($"<skill>\n{Better}</skill>", new OffTheRecordTokens(2400, 0));

        var result = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", " Don't report style. ", "The request: review this branch"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new SkillProposal(Name, Fleet, null, Better, 1, new WeaveFleet.Application.Workflows.DraftTokensDto(2400, 0)));
        var question = _conversation.Prompts.ShouldHaveSingleItem();
        question.ShouldContain("Don't report style.");
        question.ShouldContain("<session>\nThe request: review this branch\n</session>");
        question.ShouldContain($"<skill>\n{Fleet.TrimEnd()}\n</skill>");
        question.ShouldContain("Keep the front matter's name: fleet-code-review.");
        _runtime.OffTheRecordCalls.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            call => call.OwnerUserId.ShouldBe("owner-1"),
            call => call.Directory.ShouldBe(_folder),
            call => call.ProviderId.ShouldBe("anthropic"),
            call => call.ModelId.ShouldBe("claude-sonnet-5"));
        _conversation.Disposals.ShouldBe(1);
        (await _versions.GetAsync("owner-1", Name)).Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Improves_the_users_active_version_not_Fleets()
    {
        var mine = Fleet.Replace("Report what", "Only report what", StringComparison.Ordinal);
        await _skills.SaveVersionAsync(Name, mine, "stricter", null);
        _conversation.Answer($"<skill>{Better}</skill>");

        var result = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style."), CancellationToken.None);

        result.Value.Base.ShouldBe(mine);
        result.Value.BaseVersion.ShouldBe(1);
        _conversation.Prompts.Single().ShouldContain("Only report what");
        _conversation.Prompts.Single().ShouldNotContain("<session>");
    }

    [Fact]
    public async Task An_answer_that_breaks_the_front_matter_is_asked_about_once_more()
    {
        _conversation
            .Answer("<skill>---\nname: my-review\ndescription: Reviews.\n---\n</skill>", new OffTheRecordTokens(100, 10))
            .Answer($"<skill>{Better}</skill>", new OffTheRecordTokens(50, 40));

        var result = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style."), CancellationToken.None);

        result.Value.Asks.ShouldBe(2);
        result.Value.Content.ShouldBe(Better);
        result.Value.Tokens.ShouldBe(new WeaveFleet.Application.Workflows.DraftTokensDto(150, 50));
        _conversation.Prompts[1].ShouldStartWith("That skill can't be used: Keep the name in the front matter as fleet-code-review");
    }

    [Fact]
    public async Task A_second_broken_answer_or_no_change_is_an_error_and_the_conversation_is_still_disposed()
    {
        _conversation.Answer("no skill here").Answer("still nothing");
        var broken = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style."), CancellationToken.None);
        broken.Error.Description.ShouldStartWith("The model's answer broke the skill:");

        _conversation.Answer($"<skill>{Fleet}</skill>");
        var same = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style."), CancellationToken.None);
        same.Error.Description.ShouldBe("The model didn't change anything. Say more about what should be different.");

        _conversation.Disposals.ShouldBe(2);
    }

    [Fact]
    public async Task The_whole_conversation_is_read_in_a_fork_of_the_session_and_nothing_else_is_sent()
    {
        _conversation.Answer($"<skill>{Better}</skill>");

        var result = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style.", "ignored", WholeConversation: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _runtime.OffTheRecordCalls.ShouldBeEmpty();
        _conversation.Prompts.Single().ShouldNotContain("ignored");
    }

    [Fact]
    public async Task The_whole_conversation_waits_for_the_turn_to_end()
    {
        _activity.Update("s-1", "busy", "owner-1");

        var result = await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "No style.", WholeConversation: true), CancellationToken.None);

        result.Error.Description.ShouldBe("Wait for this session's turn to end, or don't include the whole conversation.");
    }

    [Fact]
    public async Task Says_what_to_fix_when_the_request_cant_be_asked()
    {
        (await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-1", "  "), CancellationToken.None))
            .Error.Description.ShouldBe("Say what the skill should do differently.");
        (await _sut.ImproveAsync("not-shipped", new ImproveSkillRequest("s-1", "x"), CancellationToken.None))
            .Error.Code.ShouldBe("BuiltInSkill.NotFound");
        (await _sut.ImproveAsync(Name, new ImproveSkillRequest("missing", "x"), CancellationToken.None))
            .Error.Code.ShouldBe("Session.NotFound");

        _sessions.Seed(new Session { Id = "s-2", Directory = _folder, HarnessType = "claude-code" });
        (await _sut.ImproveAsync(Name, new ImproveSkillRequest("s-2", "x"), CancellationToken.None))
            .Error.Description.ShouldBe("Improve isn't available on Claude Code: it can't ask a question off the record.");
    }

    [Theory]
    [InlineData("Here you go:\n<skill>\n---\nname: a\n---\n</skill>\nDone.", "---\nname: a\n---\n")]
    [InlineData("````markdown\n---\nname: a\n---\n```bash\nx\n```\n````", "---\nname: a\n---\n```bash\nx\n```\n")]
    [InlineData("---\nname: a\n---\nbody", "---\nname: a\n---\nbody\n")]
    [InlineData("<skill>\n\n</skill>", null)]
    public void The_skill_is_read_from_the_tags_a_fence_or_the_whole_answer(string answer, string? expected)
        => SkillImprovePrompt.SkillIn(answer).ShouldBe(expected);

    private sealed class Catalog : IBuiltInSkillCatalog
    {
        public IReadOnlyList<BuiltInSkill> Skills { get; } = [new(Name, "Reviews.")];

        public string? ContentOf(string name) => name == Name ? Fleet : null;
    }
}
