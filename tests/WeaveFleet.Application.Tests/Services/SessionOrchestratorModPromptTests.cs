using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// The <c>prompt.submit</c> hook in a prompt's path: a rewrite is what the conversation shows and the harness gets, a
/// note goes to the model alone, and nothing a mod does can stop the prompt.
/// </summary>
public sealed class SessionOrchestratorModPromptTests : IAsyncDisposable
{
    private const string SessionId = "s1";

    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harnessSession = new("inst-1");
    private readonly FakeTimeProvider _time = new();
    private readonly Answering _host = new();

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    private SessionOrchestrator Build(bool takesModelNotes = true)
    {
        _builder.RegisterHarness("noted", capabilities: new HarnessCapabilities { TakesModelNotes = takesModelNotes });
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active",
            HarnessType = "noted",
            UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _harnessSession);
        _builder.WithModPrompts(new ModPromptHook(_host, _time, NullLogger<ModPromptHook>.Instance));
        return _builder.Build();
    }

    private string BubbleText()
    {
        var broadcast = _builder.EventBroadcaster.Broadcasts.Single(b => b.Topic == $"session:{SessionId}" && b.Type == "message.updated");
        return string.Concat(broadcast.Payload.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task A_rewrite_is_what_the_bubble_shows_and_the_harness_gets()
    {
        _host.Result = """{"sessionId":"s1","text":"Which git branch is this session on?","context":[]}""";
        var sut = Build();

        (await sut.PromptSessionAsync(SessionId, "which branch?", null)).IsSuccess.ShouldBeTrue();

        var sent = _harnessSession.SendPromptCalls.ShouldHaveSingleItem();
        sent.Text.ShouldBe("Which git branch is this session on?");
        sent.Options!.ModelNotes.ShouldBeNull();
        BubbleText().ShouldBe("Which git branch is this session on?");
    }

    private JsonElement BubbleInfo() =>
        _builder.EventBroadcaster.Broadcasts.Single(b => b.Topic == $"session:{SessionId}" && b.Type == "message.updated").Payload.GetProperty("info");

    [Fact]
    public async Task A_rewrite_is_marked_with_the_mod_and_kept_by_the_messages_id()
    {
        _host.Result = """{"sessionId":"s1","text":"Which git branch is this session on?","context":[],"mod":"lgtm-expander"}""";
        var sut = Build();

        await sut.PromptSessionAsync(SessionId, "which branch?", null);

        BubbleInfo().GetProperty("mod").GetProperty("kind").GetString().ShouldBe("rewritten");
        BubbleInfo().GetProperty("mod").GetProperty("name").GetString().ShouldBe("lgtm-expander");
        (await _builder.MessageRepository.GetModMarksAsync(SessionId))[BubbleInfo().GetProperty("id").GetString()!].ShouldBe(new ModMark(ModMark.Rewritten, "lgtm-expander"));
    }

    [Fact]
    public async Task A_prompt_a_mod_queued_says_it_was_sent_by_that_mod_and_notes_alone_add_no_mark()
    {
        _host.Result = """{"sessionId":"s1","text":"Run the tests","context":[]}""";
        var sut = Build();

        await sut.PromptSessionAsync(SessionId, "Run the tests", new PromptOptions { SentByMod = "tests-after-edits" });

        BubbleInfo().GetProperty("mod").GetProperty("kind").GetString().ShouldBe("sent");
        BubbleInfo().GetProperty("mod").GetProperty("name").GetString().ShouldBe("tests-after-edits");

        _builder.EventBroadcaster.Broadcasts.Clear();
        _host.Result = """{"sessionId":"s1","text":"which branch?","context":["A note."]}""";
        await sut.PromptSessionAsync(SessionId, "which branch?", null);
        BubbleInfo().TryGetProperty("mod", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Context_reaches_the_model_notes_alone_not_the_bubble_or_the_text()
    {
        _host.Result = """{"sessionId":"s1","text":"which branch?","context":["The session's current git branch is feature/berths."]}""";
        var sut = Build();

        await sut.PromptSessionAsync(SessionId, "which branch?", null);

        var sent = _harnessSession.SendPromptCalls.ShouldHaveSingleItem();
        sent.Text.ShouldBe("which branch?");
        sent.Options!.ModelNotes.ShouldBe(["The session's current git branch is feature/berths."]);
        BubbleText().ShouldBe("which branch?");
    }

    [Fact]
    public async Task A_harness_that_takes_no_model_notes_gets_no_context()
    {
        _host.Result = """{"sessionId":"s1","text":"which branch?","context":["A note."]}""";
        var sut = Build(takesModelNotes: false);

        await sut.PromptSessionAsync(SessionId, "which branch?", null);

        _harnessSession.SendPromptCalls.ShouldHaveSingleItem().Options!.ModelNotes.ShouldBeNull();
    }

    [Fact]
    public async Task A_dispatch_that_never_answers_sends_the_prompt_as_typed_after_2_seconds()
    {
        _host.Pending = new TaskCompletionSource<ModDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = Build();

        var prompt = sut.PromptSessionAsync(SessionId, "which branch?", null);
        _harnessSession.SendPromptCalls.ShouldBeEmpty();
        _time.Advance(ModPromptHook.Wait);

        (await prompt.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.ShouldBeTrue();
        var sent = _harnessSession.SendPromptCalls.ShouldHaveSingleItem();
        sent.Text.ShouldBe("which branch?");
        sent.Options!.ModelNotes.ShouldBeNull();
    }

    [Fact]
    public async Task Mods_off_or_no_hook_changes_nothing()
    {
        var sut = Build();

        await sut.PromptSessionAsync(SessionId, "which branch?", null);

        _harnessSession.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe("which branch?");
        BubbleText().ShouldBe("which branch?");
    }

    private sealed class Answering : IModHost
    {
        public string? Result { get; set; }

        public TaskCompletionSource<ModDispatchResult>? Pending { get; set; }

        public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
            => Pending?.Task
               ?? Task.FromResult(Result is null
                   ? ModDispatchResult.NotDispatched
                   : new ModDispatchResult(true, JsonDocument.Parse(Result).RootElement.Clone(), []));

        public ModHostStatus GetStatus(string userId) => throw new NotSupportedException();
        public Task EnsureAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<ModSiteHooks> SitesFor(string userId, string sessionId) => throw new NotSupportedException();
        public ModLoadProblem? GetLoadProblem(string userId, string modId) => throw new NotSupportedException();
    }
}
