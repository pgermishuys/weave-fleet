using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>Which tool-row sites a session's mods hook, how a render is dispatched and answered, and how redraws are scoped and sent.</summary>
public sealed class ModSitesTests : IDisposable
{
    private const string Session = "ses_test1";
    private const string User = "test-user";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModHookSpec Render(string? matcher = null) => new("ui.render", matcher is null ? null : J(matcher));

    private static ModRoute Kept(string name, params ModHookSpec[] hooks) => new($"{name}@v1", name, null, hooks);

    private static ModRoute Draft(string name, string session, params ModHookSpec[] hooks) => new($"{name}@draft:{session}", name, session, hooks);

    private const string Chips = """{ "component": ["ToolUse", "ToolResult"], "props": { "tool": ["bash", "shell"] } }""";

    private static string Show(IReadOnlyList<ModSiteHooks> sites)
        => string.Join(";", sites.Select(s => $"{s.Site}={(s.Tools is null ? "*" : string.Join(",", s.Tools))}"));

    // ── Sites ───────────────────────────────────────────────────────────

    [Fact]
    public void The_chips_matcher_hooks_both_sites_for_shell_tools()
        => Show(ModRouting.SitesFor([Kept("test-chips", Render(Chips))], Session)).ShouldBe("ToolUse=bash,shell;ToolResult=bash,shell");

    [Theory]
    [InlineData("""{ "component": "ToolResult", "props": { "tool": "read" } }""", "ToolResult=read")]
    [InlineData("""{ "component": "ToolUse" }""", "ToolUse=*")]
    [InlineData("""{ "props": { "tool": ["edit"] } }""", "ToolUse=edit;ToolResult=edit")]
    [InlineData("""{ "component": "ToolUse", "props": { "tool": { "$regex": "^mcp__", "flags": "" } } }""", "ToolUse=*")]
    [InlineData("""{ "component": "ToolUse", "props": { "tool": ["bash", { "$regex": "x", "flags": "" }] } }""", "ToolUse=*")]
    [InlineData("""{ "component": { "$regex": "Tool", "flags": "" }, "props": { "tool": "bash" } }""", "ToolUse=bash;ToolResult=bash")]
    [InlineData("""{ "component": "StatusChip" }""", "")]
    public void A_matcher_says_which_components_and_tools(string matcher, string expected)
        => Show(ModRouting.SitesFor([Kept("demo-mod", Render(matcher))], Session)).ShouldBe(expected);

    [Fact]
    public void No_matcher_hooks_every_tool_at_both_sites()
        => Show(ModRouting.SitesFor([Kept("demo-mod", Render())], Session)).ShouldBe("ToolUse=*;ToolResult=*");

    [Fact]
    public void Sites_merge_across_hooks_and_mods_and_any_tool_wins()
    {
        var mods = new[]
        {
            Kept("aaa-mod", Render("""{ "component": "ToolUse", "props": { "tool": "bash" } }""")),
            Kept("bbb-mod", Render("""{ "props": { "tool": ["bash", "read"] } }"""), Render("""{ "component": "ToolResult", "props": { "tool": "edit" } }""")),
        };
        Show(ModRouting.SitesFor(mods, Session)).ShouldBe("ToolUse=bash,read;ToolResult=bash,read,edit");

        Show(ModRouting.SitesFor([.. mods, Kept("ccc-mod", Render("""{ "component": "ToolUse" }"""))], Session)).ShouldBe("ToolUse=*;ToolResult=bash,read,edit");
    }

    [Fact]
    public void Hooks_for_other_events_are_ignored()
        => ModRouting.SitesFor([Kept("demo-mod", new ModHookSpec("session.start", null), new ModHookSpec("ui.press", null))], Session).ShouldBeEmpty();

    [Fact]
    public void Only_this_sessions_draft_counts_and_it_takes_the_kept_mods_place()
    {
        var kept = Kept("test-chips", Render(Chips));
        var own = Draft("test-chips", Session, Render("""{ "component": "ToolUse", "props": { "tool": "read" } }"""));
        var other = Draft("other-mod", "ses_test2", Render());

        Show(ModRouting.SitesFor([kept, own, other], Session)).ShouldBe("ToolUse=read");
        Show(ModRouting.SitesFor([kept, own, other], "ses_test3")).ShouldBe("ToolUse=bash,shell;ToolResult=bash,shell");
        Show(ModRouting.SitesFor([other], Session)).ShouldBe("");
    }

    [Fact]
    public void A_host_that_isnt_running_hooks_nothing()
    {
        var supervisor = new ModHostSupervisor(User, new ModHostDependencies(
            new ModHostOptions(), new FakeFactory(), new FakeGate(), new FakeBun(), new FakeFiles(), new InMemoryModVersionStore(), new FakeStrikes(), new FakeUi(),
            (_, _, _) => Task.FromResult<Domain.Entities.Session?>(null), new FakeTimeProvider(), NullLogger.Instance));

        supervisor.SitesFor(Session).ShouldBeEmpty();
    }

    // ── Render ──────────────────────────────────────────────────────────

    private sealed class FakeHost(Func<ModDispatchRequest, ModDispatchResult> answer) : IModHost
    {
        public List<(string User, ModDispatchRequest Request)> Dispatched { get; } = [];

        public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
        {
            Dispatched.Add((userId, request));
            return Task.FromResult(answer(request));
        }

        public IReadOnlyList<ModSiteHooks> SitesFor(string userId, string sessionId) => [new("ToolUse", ["bash"])];
        public ModHostStatus GetStatus(string userId) => throw new NotSupportedException();
        public Task EnsureAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public ModLoadProblem? GetLoadProblem(string userId, string modId) => throw new NotSupportedException();
    }

    private static ModRenderRequest Request(string props, string site = "ToolUse", string? requestId = "call_1")
        => new(site, requestId, J(props));

    private const string Props = """{ "tool": "bash", "rawTool": "Bash", "category": "shell", "status": "completed", "title": "dotnet test", "input": { "command": "dotnet test" }, "inputTruncated": false, "output": "Passed!", "outputTruncated": false, "durationMs": 1200 }""";

    private static ModRenderService Service(FakeHost host) => new(host, new TestUserContext(User));

    [Fact]
    public async Task A_render_dispatches_ui_render_with_the_event_mods_see()
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        await Service(host).RenderAsync(Session, Request(Props, "ToolResult"));

        var (user, request) = host.Dispatched.ShouldHaveSingleItem();
        (user, request.Event, request.SessionId).ShouldBe((User, "ui.render", Session));
        request.E.GetProperty("component").GetString().ShouldBe("ToolResult");
        request.E.GetProperty("sessionId").GetString().ShouldBe(Session);
        request.E.GetProperty("requestId").GetString().ShouldBe("call_1");
        var props = request.E.GetProperty("props");
        props.GetProperty("tool").GetString().ShouldBe("bash");
        props.GetProperty("input").GetProperty("command").GetString().ShouldBe("dotnet test");
        props.GetProperty("output").GetString().ShouldBe("Passed!");
        props.GetProperty("durationMs").GetInt32().ShouldBe(1200);
        props.GetProperty("inputTruncated").GetBoolean().ShouldBeFalse();
    }

    // ── Controls ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("press", "ui.press", null)]
    [InlineData("input", "ui.input", "change")]
    [InlineData("submit", "ui.input", "submit")]
    [InlineData("select", "ui.select", null)]
    public async Task A_used_control_is_dispatched_with_its_handle_and_surface(string kind, string ev, string? eventKind)
    {
        var host = new FakeHost(_ => new ModDispatchResult(true, J("{}"), []));

        var view = (await Service(host).ActAsync(Session, new ModActionRequest("h17", kind, "abc", "phone"))).Value;

        view.Handled.ShouldBeTrue();
        var (user, request) = host.Dispatched.ShouldHaveSingleItem();
        (user, request.Event, request.SessionId, request.Surface).ShouldBe((User, ev, Session, "phone"));
        request.E.GetProperty("handle").GetString().ShouldBe("h17");
        request.E.GetProperty("value").GetString().ShouldBe("abc");
        (request.E.TryGetProperty("kind", out var k) ? k.GetString() : null).ShouldBe(eventKind);
    }

    [Fact]
    public async Task A_control_nobody_can_take_is_not_handled_so_the_browser_draws_again()
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        var result = await Service(host).ActAsync(Session, new ModActionRequest("h_gone", "press", null, null));

        result.Value.Handled.ShouldBeFalse();
        host.Dispatched.Single().Request.Surface.ShouldBe("desktop");
    }

    [Theory]
    [InlineData("h1", "click", null, "Kind")]
    [InlineData(null, "press", null, "Handle")]
    [InlineData("h1", "press", "tablet", "Surface")]
    public async Task A_bad_action_is_refused_before_it_reaches_the_host(string? handle, string kind, string? surface, string field)
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        var result = await Service(host).ActAsync(Session, new ModActionRequest(handle, kind, null, surface));

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain($"\"{field.ToLowerInvariant()}\"");
        host.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_dispatched_and_Fleet_both_draw_nothing_and_name_no_mods()
    {
        var notDispatched = await Service(new FakeHost(_ => ModDispatchResult.NotDispatched)).RenderAsync(Session, Request(Props));
        var fleet = await Service(new FakeHost(_ => new ModDispatchResult(true, J("""{ "type": "Fleet" }"""), []))).RenderAsync(Session, Request(Props));

        foreach (var view in new[] { notDispatched.Value, fleet.Value })
        {
            view.Tree.ShouldBeNull();
            view.Mods.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_mod_answering_null_draws_nothing_but_is_named()
    {
        var host = new FakeHost(_ => new ModDispatchResult(true, J("null"), ["test-chips@v3"]));

        var view = (await Service(host).RenderAsync(Session, Request(Props))).Value;

        view.Tree.ShouldBeNull();
        view.Mods.ShouldBe([new ModRenderedBy("test-chips", false)]);
    }

    [Fact]
    public async Task A_tree_comes_back_with_the_mods_that_drew_it_and_drafts_are_marked()
    {
        var tree = J("""{ "type": "Pill", "props": { "tone": "good", "label": "3 passed" } }""");
        var host = new FakeHost(_ => new ModDispatchResult(true, tree, ["test-chips@v1", "demo-mod@draft:ses_test1"]));

        var view = (await Service(host).RenderAsync(Session, Request(Props))).Value;

        view.Tree!.Value.GetRawText().ShouldBe(tree.GetRawText());
        view.Mods.ShouldBe([new ModRenderedBy("test-chips", false), new ModRenderedBy("demo-mod", true)]);
    }

    [Fact]
    public async Task A_large_input_becomes_an_empty_object_and_says_so_and_a_long_output_keeps_its_end()
    {
        var big = new string('x', ModRenderService.MaxInputBytes);
        var output = "head-" + new string('a', ModRenderService.MaxOutputChars) + "-tail";
        var props = JsonSerializer.Serialize(new { tool = "bash", input = new { command = big }, inputTruncated = false, output, outputTruncated = false });
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        await Service(host).RenderAsync(Session, Request(props));

        var sent = host.Dispatched.Single().Request.E.GetProperty("props");
        sent.GetProperty("input").EnumerateObject().ShouldBeEmpty();
        sent.GetProperty("inputTruncated").GetBoolean().ShouldBeTrue();
        var kept = sent.GetProperty("output").GetString()!;
        kept.Length.ShouldBe(ModRenderService.MaxOutputChars);
        kept.ShouldEndWith("-tail");
        kept.ShouldNotContain("head");
        sent.GetProperty("outputTruncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Props_within_the_caps_are_left_alone()
    {
        var props = JsonSerializer.Serialize(new { tool = "bash", input = new { command = new string('x', 1000) }, output = new string('o', ModRenderService.MaxOutputChars) });
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        await Service(host).RenderAsync(Session, Request(props));

        var sent = host.Dispatched.Single().Request.E.GetProperty("props");
        sent.GetProperty("input").GetProperty("command").GetString()!.Length.ShouldBe(1000);
        sent.GetProperty("output").GetString()!.Length.ShouldBe(ModRenderService.MaxOutputChars);
        sent.TryGetProperty("inputTruncated", out _).ShouldBeFalse();
        sent.TryGetProperty("outputTruncated", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("StatusChip", "call_1", "Validation.Site")]
    [InlineData(null, "call_1", "Validation.Site")]
    [InlineData("ToolUse", null, "Validation.RequestId")]
    [InlineData("ToolUse", "", "Validation.RequestId")]
    public async Task A_bad_request_names_the_field_and_dispatches_nothing(string? site, string? requestId, string code)
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        var result = await Service(host).RenderAsync(Session, new ModRenderRequest(site, requestId, J(Props)));

        result.Error.Code.ShouldBe(code);
        host.Dispatched.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"props\"")]
    [InlineData("[1]")]
    [InlineData("null")]
    public async Task Props_that_arent_an_object_are_refused(string props)
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        (await Service(host).RenderAsync(Session, new ModRenderRequest("ToolUse", "call_1", J(props)))).Error.Code.ShouldBe("Validation.Props");
        (await Service(host).RenderAsync(Session, new ModRenderRequest("ToolUse", "call_1", null))).Error.Code.ShouldBe("Validation.Props");
        host.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pane_render_names_its_mod_and_only_that_mod_draws_it()
    {
        var host = new FakeHost(_ => new ModDispatchResult(true, J("""{ "type": "Box", "props": {}, "children": [] }"""), ["release-board@v2"]));

        var view = (await Service(host).RenderAsync(Session, new ModRenderRequest("Pane", "board", J("""{ "title": "Release board" }"""), "release-board"))).Value;

        var request = host.Dispatched.ShouldHaveSingleItem().Request;
        (request.Event, request.Mod).ShouldBe(("ui.render", "release-board"));
        request.E.GetProperty("component").GetString().ShouldBe("Pane");
        request.E.GetProperty("requestId").GetString().ShouldBe("board");
        request.E.GetProperty("props").GetProperty("title").GetString().ShouldBe("Release board");
        view.Mods.ShouldBe([new ModRenderedBy("release-board", false)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_pane_render_without_its_mod_is_refused_naming_the_field(string? mod)
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        var result = await Service(host).RenderAsync(Session, new ModRenderRequest("Pane", "board", J("""{ "title": "Release board" }"""), mod));

        result.Error.Code.ShouldBe("Validation.Mod");
        host.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tool_rows_render_doesnt_narrow_the_chain_to_a_mod()
    {
        var host = new FakeHost(_ => ModDispatchResult.NotDispatched);

        await Service(host).RenderAsync(Session, new ModRenderRequest("ToolUse", "call_1", J(Props), "release-board"));

        host.Dispatched.ShouldHaveSingleItem().Request.Mod.ShouldBeNull();
    }

    [Fact]
    public void The_sites_are_the_hosts()
        => Service(new FakeHost(_ => ModDispatchResult.NotDispatched)).SitesFor(Session).Sites.Select(s => $"{s.Site}={string.Join(",", s.Tools!)}").ShouldBe(["ToolUse=bash"]);

    // ── Redraws ─────────────────────────────────────────────────────────

    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeUi _ui = new();
    private readonly FakeFactory _factory = new();

    public void Dispose() => _store.DeleteFolders();

    private async Task<IModHostCalls> LoadedAsync()
    {
        _store.SeedHistory(User, new ModHistory("test-chips", 1, null, [new ModVersion(1, DateTimeOffset.UnixEpoch, "0.1.0", "abc", null, null, null, null)]));
        _store.SeedDraft(User, Session, "demo-mod");
        _factory.Answers["load"] = _ => Task.FromResult(JsonSerializer.SerializeToElement(new { check = new { ok = true }, hooks = new[] { new { @event = "ui.render" } } }));
        var supervisor = new ModHostSupervisor(User, new ModHostDependencies(
            new ModHostOptions(), _factory, new FakeGate(), new FakeBun(), new FakeFiles(), _store, new FakeStrikes(), _ui,
            (_, _, _) => Task.FromResult<Domain.Entities.Session?>(null), new FakeTimeProvider(), NullLogger.Instance));
        await supervisor.EnsureAsync(CancellationToken.None).Within();
        await _factory.NextStart();
        return supervisor;
    }

    private static JsonElement Note(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task An_invalidate_from_a_mod_that_isnt_loaded_is_dropped()
    {
        var calls = await LoadedAsync();

        calls.HandleNotification("invalidate", Note(new { mod = "ghost-mod@v1", sessionId = Session }));
        calls.HandleNotification("invalidate", Note(new { mod = "test-chips@v9" }));

        _ui.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_kept_mods_invalidate_goes_to_the_session_it_names_or_to_every_session()
    {
        var calls = await LoadedAsync();

        calls.HandleNotification("invalidate", Note(new { mod = "test-chips@v1", sessionId = "ses_test2" }));
        calls.HandleNotification("invalidate", Note(new { mod = "test-chips@v1" }));

        _ui.Calls.ShouldBe(["invalidate test-chips@v1 ses_test2", "invalidate test-chips@v1 "]);
    }

    [Fact]
    public async Task A_drafts_invalidate_always_targets_its_own_session()
    {
        var calls = await LoadedAsync();
        var draft = $"demo-mod@draft:{Session}";

        calls.HandleNotification("invalidate", Note(new { mod = draft, sessionId = "ses_test2" }));
        calls.HandleNotification("invalidate", Note(new { mod = draft }));

        _ui.Calls.ShouldBe([$"invalidate {draft} {Session}", $"invalidate {draft} {Session}"]);
    }

    [Fact]
    public async Task Loading_the_users_mods_tells_the_browser_to_ask_for_sites_again()
    {
        await LoadedAsync();

        // A sample kept a moment ago reaches the browser's sites only once the host has loaded it.
        _ui.Loads.ShouldBeGreaterThan(0);
        _ui.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Fleets_own_redraw_goes_to_every_session_with_no_mod()
    {
        var events = new FakeEventBroadcaster();

        new BroadcastingModHostUi(events).Loaded(User);

        var sent = events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe(("sessions", "mod.ui", User));
        sent.DomainEvent.ShouldBeOfType<ModUi>().Payload.Mod.ShouldBeNull();
        sent.Payload.TryGetProperty("mod", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(Session, "session:ses_test1")]
    [InlineData(null, "sessions")]
    public void The_browser_hears_a_redraw_on_the_sessions_topic_or_every_sessions(string? session, string topic)
    {
        var events = new FakeEventBroadcaster();

        new BroadcastingModHostUi(events).Invalidated(User, "test-chips@v3", session);

        var sent = events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe((topic, "mod.ui", User));
        var payload = sent.DomainEvent.ShouldBeOfType<ModUi>().Payload;
        (payload.SessionId, payload.Site, payload.RequestId, payload.Mod).ShouldBe((session, null, null, "test-chips"));
        sent.Payload.GetProperty("mod").GetString().ShouldBe("test-chips");
        // Nulls are left out of the payload, as in every other event.
        sent.Payload.TryGetProperty("sessionId", out var onTheWire).ShouldBe(session is not null);
        if (session is not null)
            onTheWire.GetString().ShouldBe(session);
    }

    [Fact]
    public async Task A_pane_opened_or_closed_reaches_the_sessions_topic_as_mod_pane()
    {
        var events = new FakeEventBroadcaster();
        var ui = new BroadcastingModHostUi(events);

        await ui.OpenPaneAsync(User, "release-board@v2", Session, "board", "Release board", CancellationToken.None);
        await ui.ClosePaneAsync(User, "release-board@draft:ses_test1", Session, "board", CancellationToken.None);

        events.Broadcasts.Select(b => (b.Topic, b.Type, b.UserId)).ShouldBe([($"session:{Session}", "mod.pane", User), ($"session:{Session}", "mod.pane", User)]);
        var opened = events.Broadcasts[0].DomainEvent.ShouldBeOfType<ModPane>().Payload;
        (opened.SessionId, opened.Mod, opened.Id, opened.Title, opened.Open).ShouldBe((Session, "release-board", "board", "Release board", true));
        var closed = events.Broadcasts[1].DomainEvent.ShouldBeOfType<ModPane>().Payload;
        (closed.Mod, closed.Id, closed.Title, closed.Open).ShouldBe(("release-board", "board", null, false));
    }
}
