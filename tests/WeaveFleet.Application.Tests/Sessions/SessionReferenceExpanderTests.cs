using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Sessions;

/// <summary>
/// What an <c>@</c>-referenced session becomes in the text the agent gets, per harness: a link for one with Fleet's
/// tools, a recap for one without.
/// </summary>
public sealed class SessionReferenceExpanderTests : IAsyncDisposable
{
    private const string Text = "Use the subagent mapping from @t3code-notes";
    private static readonly SessionReference Reference = new("@t3code-notes", "ses-ref");

    private readonly InMemorySessionRepository _sessions = new();
    private readonly FakeHarnessRegistry _harnesses = new();
    private readonly InstanceTracker _instances = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FakeSessionMessageProxy _messages = new();
    private readonly FakeHarnessSession _referencedHarness = new("inst-ref");
    private readonly FakeRemoteReferences _remote = new();
    private readonly SessionRecapService _recaps;
    private readonly SessionReferenceExpander _sut;

    public SessionReferenceExpanderTests()
    {
        _harnesses.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsFleetTools = true, SupportsOffTheRecordPrompt = true }));
        _harnesses.Register(new FakeHarness("claude-code", "Claude Code", new HarnessCapabilities()));
        Insert("ses-oc", "opencode", "Capture subagents");
        Insert("ses-cc", "claude-code", "Capture Claude Code subagents");
        Insert("ses-ref", "opencode", "t3code: \"what\" & <how>?", instanceId: "inst-ref");

        var recaps = new SessionRecapService(
            _activity,
            _instances,
            _harnesses,
            new FakeEventBroadcaster(),
            new RecapsOff(),
            new SessionFocusTracker(),
            TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionRepository>(_sessions)),
            new FakeTimeProvider(),
            NullLogger<SessionRecapService>.Instance);
        _recaps = recaps;
        _sut = new SessionReferenceExpander(_sessions, _harnesses, recaps, _messages, _remote);
    }

    public ValueTask DisposeAsync() => _referencedHarness.DisposeAsync();

    [Fact]
    public async Task a_harness_with_fleet_tools_gets_a_link_to_read_with_fleet_session_read()
    {
        var result = await _sut.ExpandAsync("ses-oc", Text, [Reference]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(
            Text + "\n\n<fleet-session-references>\n" + SessionReferences.LinkNote + "\n"
            + "<session ref=\"@t3code-notes\" id=\"ses-ref\" title=\"t3code: &quot;what&quot; &amp; &lt;how&gt;?\" />\n"
            + "</fleet-session-references>");
        _referencedHarness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task for_a_session_not_made_yet_the_harness_type_decides_link_or_recap()
    {
        var link = await _sut.ExpandForHarnessAsync("opencode", Text, [Reference]);
        var recap = await _sut.ExpandForHarnessAsync("claude-code", Text, [Reference]);

        link.Value.ShouldContain(SessionReferences.LinkNote);
        recap.Value.ShouldContain(SessionReferences.RecapNote);
    }

    [Fact]
    public async Task a_harness_without_fleet_tools_gets_a_recap_written_by_the_referenced_session()
    {
        // Running, idle and able to answer off the record: it writes one, though recaps are off for the user.
        _instances.Register("inst-ref", _referencedHarness);
        _referencedHarness.OffTheRecordAnswer = "It mapped each subagent to a child <thread>.";

        var result = await _sut.ExpandAsync("ses-cc", Text, [Reference]);

        result.Value.ShouldBe(
            Text + "\n\n<fleet-session-references>\n" + SessionReferences.RecapNote + "\n"
            + "<session ref=\"@t3code-notes\" id=\"ses-ref\" title=\"t3code: &quot;what&quot; &amp; &lt;how&gt;?\">\n"
            + "It mapped each subagent to a child &lt;thread&gt;.\n"
            + "</session>\n</fleet-session-references>");
        _referencedHarness.OffTheRecordPrompts.ShouldBe([SessionRecapService.ReferencePrompt]);
    }

    [Fact]
    public async Task without_a_running_harness_the_recap_is_the_sessions_last_request_and_reply()
    {
        _messages.GetMessagesBehavior = (sessionId, _, _, _) => Task.FromResult(new MessagePage(
            sessionId == "ses-ref"
                ? [Message("m1", "user", "How does t3code map subagents?"), Message("m2", "assistant", "Each subagent\nis a child thread."), Message("m3", "user", "Write it up.")]
                : [],
            HasMore: false));

        var result = await _sut.ExpandAsync("ses-cc", Text, [Reference]);

        result.Value.ShouldContain(
            "No recap was written; its latest messages instead.\nLast request: Write it up.\nLast reply: Each subagent is a child thread.\n</session>");
        _referencedHarness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_busy_referenced_session_is_not_asked_for_a_recap()
    {
        _instances.Register("inst-ref", _referencedHarness);
        _referencedHarness.OffTheRecordAnswer = "Should not be asked.";
        _activity.Update("ses-ref", "busy", "u1");

        var result = await _sut.ExpandAsync("ses-cc", Text, [Reference]);

        result.Value.ShouldContain("No recap: this session has no messages yet.");
        _referencedHarness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task references_whose_token_left_the_text_are_left_out()
    {
        var result = await _sut.ExpandAsync("ses-oc", "Never mind @t3code-notes-2 and me@t3code-notes", [Reference]);

        result.Value.ShouldBe("Never mind @t3code-notes-2 and me@t3code-notes");
    }

    [Fact]
    public async Task a_message_without_references_goes_as_typed()
    {
        (await _sut.ExpandAsync("ses-oc", Text, null)).Value.ShouldBe(Text);
        (await _sut.ExpandAsync("ses-oc", Text, [])).Value.ShouldBe(Text);
    }

    [Fact]
    public async Task a_session_fleet_cannot_find_for_the_user_refuses_the_message()
    {
        var result = await _sut.ExpandAsync("ses-oc", Text, [Reference with { SessionId = "ses-someone-elses" }]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionReferences");
        result.Error.Description.ShouldBe("@t3code-notes names a session Fleet can't find. Pick it again from the @ list.");
    }

    [Theory]
    [InlineData("t3code-notes")]
    [InlineData("@t3code\" id=\"x")]
    [InlineData("@")]
    public async Task a_token_that_could_break_the_block_is_refused(string token)
    {
        var result = await _sut.ExpandAsync("ses-oc", $"See {token}", [new SessionReference(token, "ses-ref")]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionReferences");
    }

    // ── Sessions on other machines ──────────────────────────────────────────

    [Fact]
    public async Task a_reference_naming_this_machine_reads_exactly_as_one_naming_none()
    {
        var plain = await _sut.ExpandAsync("ses-oc", Text, [Reference]);
        var here = await _sut.ExpandAsync("ses-oc", Text, [Reference with { MachineId = FakeRemoteReferences.Here }]);
        var withoutRemote = await new SessionReferenceExpander(_sessions, _harnesses, _recaps, _messages).ExpandAsync("ses-oc", Text, [Reference]);

        here.Value.ShouldBe(plain.Value);
        withoutRemote.Value.ShouldBe(plain.Value);
        plain.Value.ShouldContain(SessionReferences.LinkNote);
        plain.Value.ShouldNotContain("machine=");
        _remote.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task another_machines_session_names_its_machine_and_says_to_read_it_there()
    {
        const string text = "Use @t3code-notes and the backoff from @tide-chart-caching";
        var result = await _sut.ExpandAsync("ses-oc", text, [Reference, new SessionReference("@tide-chart-caching", "ses-far", "machine-kestrel")]);

        result.Value.ShouldBe(
            text + "\n\n<fleet-session-references>\n" + SessionReferences.LinkNoteWithMachines + "\n"
            + "<session ref=\"@t3code-notes\" id=\"ses-ref\" title=\"t3code: &quot;what&quot; &amp; &lt;how&gt;?\" />\n"
            + "<session ref=\"@tide-chart-caching\" id=\"ses-far\" title=\"Tide chart caching\" machine=\"kestrel\" />\n"
            + "</fleet-session-references>");
        _remote.Asked.ShouldBe([("machine-kestrel", "ses-far", false)]);
    }

    [Fact]
    public async Task a_harness_without_fleet_tools_gets_the_recap_its_machine_sends_or_a_link_without_one()
    {
        const string text = "See @tide-chart-caching";
        var withRecap = await _sut.ExpandAsync("ses-cc", text, [new SessionReference("@tide-chart-caching", "ses-far", "machine-kestrel")]);
        _remote.Recap = null;
        var withoutRecap = await _sut.ExpandAsync("ses-cc", text, [new SessionReference("@tide-chart-caching", "ses-far", "machine-kestrel")]);

        withRecap.Value.ShouldContain(
            "<session ref=\"@tide-chart-caching\" id=\"ses-far\" title=\"Tide chart caching\" machine=\"kestrel\">\n"
            + "It cached charts per harbour.\n</session>");
        withoutRecap.Value.ShouldContain("<session ref=\"@tide-chart-caching\" id=\"ses-far\" title=\"Tide chart caching\" machine=\"kestrel\" />");
        _remote.Asked.ShouldAllBe(asked => asked.WithRecap);
    }

    [Fact]
    public async Task a_session_its_machine_cannot_vouch_for_refuses_the_message_with_why()
    {
        _remote.Error = "is a session on hangar, which agents on falcon can't use.";

        var result = await _sut.ExpandAsync("ses-oc", "See @runway", [new SessionReference("@runway", "ses-far", "machine-hangar")]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionReferences");
        result.Error.Description.ShouldBe("@runway is a session on hangar, which agents on falcon can't use.");
    }

    [Fact]
    public async Task without_a_way_to_other_machines_a_reference_to_one_is_refused()
    {
        var sut = new SessionReferenceExpander(_sessions, _harnesses, _recaps, _messages);

        var result = await sut.ExpandAsync("ses-oc", "See @runway", [new SessionReference("@runway", "ses-far", "machine-hangar")]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionReferences");
    }

    private void Insert(string id, string harnessType, string title, string instanceId = "inst-x")
        => _sessions.InsertAsync(new Session { Id = id, HarnessType = harnessType, Title = title, InstanceId = instanceId, UserId = "u1" })
            .GetAwaiter().GetResult();

    private static HarnessMessage Message(string id, string role, string text) => new()
    {
        Id = id,
        Role = role,
        Parts = [new TextPart(text)],
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeRemoteReferences : IRemoteSessionReferences
    {
        public const string Here = "machine-falcon";

        public string? Recap { get; set; } = "It cached charts per harbour.";
        public string? Error { get; set; }
        public List<(string MachineId, string SessionId, bool WithRecap)> Asked { get; } = [];

        public string LocalMachineId => Here;

        public Task<RemoteSessionReference> FindAsync(string machineId, string sessionId, bool withRecap, CancellationToken ct = default)
        {
            Asked.Add((machineId, sessionId, withRecap));
            return Task.FromResult(Error is not null
                ? RemoteSessionReference.Failed(Error)
                : new RemoteSessionReference("kestrel", "Tide chart caching", withRecap ? Recap : null, null));
        }
    }

    private sealed class RecapsOff : IRecapPreference
    {
        public Task<bool> IsEnabledAsync(string userId, CancellationToken ct) => Task.FromResult(false);
    }
}
