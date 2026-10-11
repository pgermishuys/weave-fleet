using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Tests.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>The <c>prompt.submit</c> hook fails open: whatever goes wrong, the prompt goes on as typed.</summary>
public sealed class ModPromptHookTests
{
    private const string Typed = "which branch am I on?";

    private readonly ScriptedHost _host = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ModPromptHook _hook;

    public ModPromptHookTests() => _hook = new ModPromptHook(_host, _time, NullLogger<ModPromptHook>.Instance);

    private Task<ModPromptOutcome> Apply() => _hook.ApplyAsync("user-1", "ses_1", Typed, CancellationToken.None).Within();

    private static ModDispatchResult Answer(string json) => new(true, JsonDocument.Parse(json).RootElement.Clone(), []);

    [Fact]
    public async Task Sends_the_typed_text_with_no_context_and_returns_the_rewrite_and_notes()
    {
        _host.Reply = _ => Task.FromResult(Answer("""{"sessionId":"ses_1","text":"On which branch am I?","context":["Branch is main."]}"""));

        var outcome = await Apply();

        outcome.Text.ShouldBe("On which branch am I?");
        outcome.Context.ShouldBe(["Branch is main."]);
        var (user, request) = _host.Requests.ShouldHaveSingleItem();
        (user, request.Event, request.SessionId).ShouldBe(("user-1", "prompt.submit", "ses_1"));
        request.E.GetRawText().ShouldBe("""{"sessionId":"ses_1","text":"which branch am I on?","context":[]}""");
    }

    [Fact]
    public async Task No_mod_hooking_it_means_no_change_and_no_wait()
    {
        (await Apply()).ShouldBe(ModPromptOutcome.Unchanged(Typed), "NotDispatched is the default answer");
    }

    [Fact]
    public async Task A_host_that_throws_leaves_the_prompt_alone()
    {
        _host.Reply = _ => throw new InvalidOperationException("pipe closed");

        (await Apply()).ShouldBe(ModPromptOutcome.Unchanged(Typed));
    }

    [Theory]
    [InlineData("""[1]""")]
    [InlineData("""null""")]
    [InlineData("""{"context":["x"]}""")]
    [InlineData("""{"text":5,"context":["x"]}""")]
    [InlineData("""{"text":"   ","context":["x"]}""")]
    [InlineData("""{"text":"","context":["x"]}""")]
    public async Task An_answer_that_is_not_an_object_with_a_text_leaves_the_prompt_alone(string result)
    {
        _host.Reply = _ => Task.FromResult(Answer(result));

        (await Apply()).ShouldBe(ModPromptOutcome.Unchanged(Typed));
    }

    [Fact]
    public async Task A_missing_result_leaves_the_prompt_alone()
    {
        _host.Reply = _ => Task.FromResult(new ModDispatchResult(true, null, []));

        (await Apply()).ShouldBe(ModPromptOutcome.Unchanged(Typed));
    }

    [Fact]
    public async Task Names_the_mod_the_host_says_rewrote_the_text()
    {
        _host.Reply = _ => Task.FromResult(Answer("""{"text":"Hi","context":[],"mod":"lgtm-expander"}"""));
        (await Apply()).Mod.ShouldBe("lgtm-expander");

        _host.Reply = _ => Task.FromResult(Answer("""{"text":"Hi","context":[]}"""));
        (await Apply()).Mod.ShouldBeNull();
    }

    [Fact]
    public async Task Context_that_is_not_a_list_is_ignored_and_non_strings_in_it_are_skipped()
    {
        _host.Reply = _ => Task.FromResult(Answer("""{"text":"Hi","context":"nope"}"""));
        var plain = await Apply();
        plain.Text.ShouldBe("Hi");
        plain.Context.ShouldBeEmpty();

        _host.Reply = _ => Task.FromResult(Answer("""{"text":"Hi","context":["a",2,null,"b"]}"""));
        (await Apply()).Context.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Context_is_capped_at_8_KiB_and_entries_past_the_cap_are_dropped()
    {
        var big = new string('x', 5 * 1024);
        _host.Reply = _ => Task.FromResult(Answer($$"""{"text":"Hi","context":["{{big}}","{{big}}","tiny"]}"""));

        (await Apply()).Context.ShouldBe([big]);
    }

    [Fact]
    public async Task Waiting_longer_than_2_seconds_stops_waiting_without_killing_the_host_and_watches_the_dispatch()
    {
        var never = new TaskCompletionSource<ModDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Reply = _ => never.Task;

        var pending = Apply();
        pending.IsCompleted.ShouldBeFalse();
        _time.Advance(ModPromptHook.Wait - TimeSpan.FromMilliseconds(1));
        pending.IsCompleted.ShouldBeFalse("a hook still has its last millisecond");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await pending).ShouldBe(ModPromptOutcome.Unchanged(Typed));
        // The dispatch finishes later, even failing, and nothing is left unobserved or changed.
        never.SetException(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task Cancelling_the_prompt_cancels_the_wait()
    {
        _host.Reply = _ => new TaskCompletionSource<ModDispatchResult>().Task;
        using var cts = new CancellationTokenSource();

        var pending = _hook.ApplyAsync("user-1", "ses_1", Typed, cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(pending.Within());
    }

    internal sealed class ScriptedHost : IModHost
    {
        public Func<ModDispatchRequest, Task<ModDispatchResult>> Reply { get; set; } = _ => Task.FromResult(ModDispatchResult.NotDispatched);

        public List<(string User, ModDispatchRequest Request)> Requests { get; } = [];

        public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
        {
            Requests.Add((userId, request));
            return Reply(request);
        }

        public ModHostStatus GetStatus(string userId) => throw new NotSupportedException();
        public Task EnsureAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<ModSiteHooks> SitesFor(string userId, string sessionId) => throw new NotSupportedException();
        public ModLoadProblem? GetLoadProblem(string userId, string modId) => throw new NotSupportedException();
    }
}
