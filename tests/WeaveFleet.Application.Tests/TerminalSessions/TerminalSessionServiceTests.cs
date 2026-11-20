using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;
using Terminal = WeaveFleet.Application.TerminalSessions.TerminalSessions;

namespace WeaveFleet.Application.Tests.TerminalSessions;

/// <summary>
/// A terminal Claude Code session brought into Fleet: only when it's on, only for a folder in a repository Fleet knows,
/// never for a session Fleet started, never twice, and its turns saved once however often they're read.
/// </summary>
public sealed class TerminalSessionServiceTests : IDisposable
{
    private const string ClaudeSessionId = "0b6f1c1e-2a2d-4f4e-9a55-1d3c4e5f6a70";

    private readonly RealGitRepository _repository = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryMessageRepository _messages = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly InMemoryInstanceRepository _instances = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly InMemoryOutboxRepository _outbox = new();
    private readonly StubTranscripts _transcripts = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero));
    private readonly TerminalSessionPresence _presence;
    private readonly TerminalSessionService _service;

    public TerminalSessionServiceTests()
    {
        var user = new TestUserContext();
        var roots = new InMemoryWorkspaceRootRepository();
        roots.Seed(new WorkspaceRoot { Id = "root-1", Path = _repository.ParentPath, CreatedAt = DateTime.UtcNow.ToString("O") });
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IWorkspaceRootRepository>(roots);
            services.AddSingleton<IUserContext>(user);
            services.AddScoped(_ => new WorkspaceRootService(roots, user));
        });

        _presence = new TerminalSessionPresence(_time);
        var projects = new InMemoryProjectRepository();
        projects.Seed(new Project { Id = "scratch", Name = "Scratch", Type = "scratch" });
        _service = new TerminalSessionService(
            new TerminalSessionsFeature(_preferences),
            _sessions,
            _messages,
            projects,
            new WorkspaceService(_workspaces, user, new FleetOptions(), NullLogger<WorkspaceService>.Instance),
            new InstanceService(_instances, _sessions, user),
            new RepositoryService(scopes, NullLogger<RepositoryService>.Instance),
            _transcripts,
            _presence,
            _broadcaster,
            new SessionActivityWriteService(
                new FakeDbConnectionFactory(), _messages, new InMemoryDelegationRepository(), _sessions, new InMemorySmartLinkRepository(), _outbox, new FakeOutboxDispatcher()),
            user,
            NullLogger<TerminalSessionService>.Instance);

        _transcripts.Current = Transcript(Turn("u1", "user", "Where do the crates go?", 1), Turn("a1", "assistant", "To the shed.", 2));
    }

    public void Dispose() => _repository.Dispose();

    private void TurnOn() => _preferences.Seed(Terminal.PreferenceKey, "true");

    private Task<TerminalSessionAnswer> Report(string kind, string? folder = null, string? id = ClaudeSessionId)
        => _service.ReportAsync(new TerminalSessionReport(kind, id, folder ?? _repository.Path), CancellationToken.None);

    [Fact]
    public async Task Nothing_is_kept_while_the_switch_is_off()
    {
        var answer = await Report("started");

        answer.Status.ShouldBe("off");
        _sessions.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_in_a_known_repository_is_added_as_a_claude_code_session_with_its_history()
    {
        TurnOn();

        var answer = await Report("started");

        answer.Status.ShouldBe("added");
        var session = _sessions.All.ShouldHaveSingleItem();
        session.HarnessType.ShouldBe("claude-code");
        session.HarnessResumeToken.ShouldBe(ClaudeSessionId);
        session.SpawnKind.ShouldBe(SpawnKinds.Terminal);
        session.Title.ShouldBe("Where do the crates go?");
        session.Directory.ShouldBe(WorkspaceRootService.CanonicalizePath(_repository.Path));
        session.Status.ShouldBe("stopped");
        _messages.All.Select(m => m.Role).ShouldBe(["user", "assistant"], ignoreOrder: true);
        _broadcaster.Broadcasts.ShouldContain(b => b.Type == "session_created");
    }

    [Fact]
    public async Task A_report_for_a_session_already_added_adds_nothing_and_saves_each_turn_once()
    {
        TurnOn();
        await Report("started");

        _transcripts.Current = Transcript(
            Turn("u1", "user", "Where do the crates go?", 1),
            Turn("a1", "assistant", "To the shed.", 2),
            Turn("u2", "user", "And the ladders?", 3),
            Turn("a2", "assistant", "Beside them.", 4));
        var answer = await Report("ended");
        await Report("ended");

        answer.Status.ShouldBe("updated");
        _sessions.All.ShouldHaveSingleItem();
        _messages.All.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_heartbeat_keeps_a_session_open_and_shows_the_turns_written_since()
    {
        TurnOn();
        await Report("started");
        _transcripts.Current = Transcript(
            Turn("u1", "user", "Where do the crates go?", 1),
            Turn("a1", "assistant", "To the shed.", 2),
            Turn("u2", "user", "And the ladders?", 3));

        var answer = await Report("heartbeat");

        answer.Status.ShouldBe("present");
        _presence.IsOpen(ClaudeSessionId).ShouldBeTrue();
        _messages.All.Count.ShouldBe(3);
        // The open conversation is told, as for any turn: the message, then its part.
        _outbox.All.Select(m => m.Type).ShouldBe(["message.updated", "message.part.updated"]);
        _outbox.All.ShouldAllBe(m => m.Topic == $"session:{_sessions.All.Single().Id}");
    }

    [Fact]
    public async Task A_change_in_the_transcript_shows_new_turns_without_waiting_for_a_heartbeat_and_a_closed_session_is_not_read()
    {
        TurnOn();
        await Report("started");
        _transcripts.Current = Transcript(Turn("u1", "user", "Where do the crates go?", 1), Turn("a1", "assistant", "To the shed.", 2), Turn("u2", "user", "And the ladders?", 3));

        await Report("changed");
        _messages.All.Count.ShouldBe(3);

        await Report("ended");
        _transcripts.Current = Transcript(Turn("u1", "user", "Where do the crates go?", 1), Turn("a1", "assistant", "To the shed.", 2), Turn("u2", "user", "And the ladders?", 3), Turn("a2", "assistant", "Beside them.", 4));
        await Report("changed");
        _messages.All.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_reply_that_grows_is_saved_again_in_place_and_shown_again()
    {
        TurnOn();
        await Report("started");
        _transcripts.Current = Transcript(Turn("u1", "user", "Where do the crates go?", 1), Turn("a1", "assistant", "To the shed. And", 2));
        await Report("changed");
        var before = _outbox.All.Count;

        _transcripts.Current = Transcript(Turn("u1", "user", "Where do the crates go?", 1), Turn("a1", "assistant", "To the shed. And the ladders.", 2));
        await Report("changed");

        _messages.All.Count.ShouldBe(2);
        _messages.All.Single(m => m.Role == "assistant").PartsJson.ShouldContain("the ladders");
        _outbox.All.Count.ShouldBe(before + 2);
    }

    [Fact]
    public async Task A_heartbeat_for_a_session_Fleet_never_heard_of_adds_it()
    {
        TurnOn();

        var answer = await Report("heartbeat");

        answer.Status.ShouldBe("added");
        _sessions.All.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_session_that_goes_silent_is_the_one_that_ended_and_one_that_says_so_is_closed_at_once()
    {
        TurnOn();
        await Report("started");
        _time.Advance(Terminal.Silence - TimeSpan.FromSeconds(1));
        _presence.TakeSilent(Terminal.Silence).ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(2));
        var silent = _presence.TakeSilent(Terminal.Silence).ShouldHaveSingleItem();
        silent.ClaudeSessionId.ShouldBe(ClaudeSessionId);
        _presence.IsOpen(ClaudeSessionId).ShouldBeFalse();

        await Report("started");
        await Report("ended");
        _presence.IsOpen(ClaudeSessionId).ShouldBeFalse();
    }

    [Fact]
    public async Task A_session_nobody_typed_in_is_removed_when_it_ends()
    {
        TurnOn();
        _transcripts.Current = null;
        (await Report("started")).Status.ShouldBe("added");
        _sessions.All.ShouldHaveSingleItem();

        var answer = await Report("ended");

        answer.Status.ShouldBe("removed");
        _sessions.All.ShouldBeEmpty();
        _broadcaster.Broadcasts.ShouldContain(b => b.Type == "session_deleted");
    }

    [Fact]
    public async Task A_session_that_ends_before_Fleet_heard_of_it_is_added_then()
    {
        TurnOn();

        var answer = await Report("ended");

        answer.Status.ShouldBe("added");
        _messages.All.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_title_the_user_gave_it_later_replaces_the_first_prompt()
    {
        TurnOn();
        await Report("started");

        _transcripts.Current = _transcripts.Current! with { Title = "Crate plan" };
        await Report("ended");

        _sessions.All.ShouldHaveSingleItem().Title.ShouldBe("Crate plan");
    }

    [Fact]
    public async Task A_worktree_of_a_known_repository_counts_wherever_it_is()
    {
        TurnOn();
        var worktree = _repository.CreateWorktree("feature-crates");
        // Beside the repository, so outside every workspace root as far as the roots are concerned.
        var outside = Path.Combine(Path.GetTempPath(), $"fleet-wt-{Guid.NewGuid():N}");
        _repository.Git("worktree", "add", outside, "-b", "feature-outside");
        try
        {
            (await Report("started", worktree, "worktree-session-1")).Status.ShouldBe("added");
            (await Report("started", outside, "worktree-session-2")).Status.ShouldBe("added");
        }
        finally
        {
            _repository.Git("worktree", "remove", "--force", outside);
        }
    }

    [Fact]
    public async Task A_folder_in_no_repository_Fleet_knows_shows_nothing()
    {
        TurnOn();
        var elsewhere = Path.Combine(Path.GetTempPath(), $"fleet-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        try
        {
            var answer = await Report("started", elsewhere);

            answer.Status.ShouldBe("unknown-folder");
            _sessions.All.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task A_session_Fleet_started_itself_is_never_added_or_touched()
    {
        TurnOn();
        _sessions.Seed(new Session
        {
            Id = "fleet-own",
            HarnessType = "claude-code",
            HarnessResumeToken = ClaudeSessionId,
            Title = "Started in Fleet",
            Directory = _repository.Path,
        });

        var answer = await Report("started");

        answer.Status.ShouldBe("fleet-session");
        answer.SessionId.ShouldBe("fleet-own");
        _sessions.All.ShouldHaveSingleItem().Title.ShouldBe("Started in Fleet");
        _messages.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Terminal_then_Fleet_then_terminal_again_imports_every_turn_once()
    {
        TurnOn();
        await Report("started");
        var session = _sessions.All.ShouldHaveSingleItem();

        // A prompt and reply Fleet ran: saved under Fleet's own ids, claimed by their transcript uuids, and in the transcript too.
        _messages.Seed(new PersistedMessage { Id = "msg_fleet_prompt", SessionId = session.Id, Role = "user", PartsJson = """[{"kind":"text","text":"From Fleet"}]""", Timestamp = Stamp(5).ToString("O"), CreatedAt = Stamp(5).ToString("O") });
        _messages.Seed(new PersistedMessage { Id = "msg_fleet_reply", SessionId = session.Id, Role = "assistant", PartsJson = """[{"kind":"text","text":"Done."}]""", Timestamp = Stamp(6).ToString("O"), CreatedAt = Stamp(6).ToString("O") });
        await _messages.ClaimTranscriptEntriesAsync(session.Id, ["u3", "a3"]);

        // Then the terminal again: a turn typed after Fleet's.
        _transcripts.Current = Transcript(
            Turn("u1", "user", "Where do the crates go?", 1),
            Turn("a1", "assistant", "To the shed.", 2),
            Turn("u3", "user", "From Fleet", 5),
            Turn("a3", "assistant", "Done.", 6),
            Turn("u4", "user", "Typed in the terminal", 7),
            Turn("a4", "assistant", "Noted.", 8));
        await Report("started");
        await Report("heartbeat");
        await Report("ended");

        _messages.All.Count.ShouldBe(6);
        _messages.All.Count(m => m.PartsJson.Contains("From Fleet")).ShouldBe(1);
        _messages.All.Count(m => m.PartsJson.Contains("Typed in the terminal")).ShouldBe(1);
        _messages.All.Count(m => m.PartsJson.Contains("Noted.")).ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "/work")]
    [InlineData("started", "")]
    [InlineData("started", "relative/path")]
    [InlineData("paused", "/work")]
    public async Task A_report_that_isnt_one_is_refused(string? kind, string folder)
    {
        TurnOn();

        var answer = await _service.ReportAsync(new TerminalSessionReport(kind, ClaudeSessionId, folder), CancellationToken.None);

        answer.Status.ShouldBe("invalid");
    }

    private static DateTimeOffset Stamp(int second) => new(2026, 11, 20, 3, 0, second, TimeSpan.Zero);

    private static readonly Dictionary<string, string> EntryOf = new(StringComparer.Ordinal);

    private static HarnessMessage Turn(string uuid, string role, string text, int second)
    {
        var message = BuildTurn(uuid, role, text, second);
        EntryOf[message.Id] = uuid;
        return message;
    }

    private static HarnessMessage BuildTurn(string uuid, string role, string text, int second) => new()
    {
        // The id a reader gives: stable for the entry. Any fixed text will do for the test.
        Id = WeaveFleet.Domain.Identity.AscendingMessageId.Stable(Stamp(second).ToUnixTimeMilliseconds(), 0, uuid),
        Role = role,
        Parts = [new TextPart(text)],
        Timestamp = Stamp(second),
    };

    private static TerminalTranscript Transcript(params HarnessMessage[] turns)
        => new(turns, Title: null, FirstPrompt: turns.FirstOrDefault(t => t.Role == "user")?.TextContent, Folder: null, Unknown: [])
        {
            Entries = turns.ToDictionary(t => t.Id, t => (IReadOnlyList<string>)[EntryOf[t.Id]], StringComparer.Ordinal),
        };

    private sealed class StubTranscripts : ITerminalTranscripts
    {
        public TerminalTranscript? Current { get; set; }

        public Task<TerminalTranscript?> ReadAsync(string sessionId, string folder, string? path, CancellationToken ct)
            => Task.FromResult(Current);

        public string? FileOf(string sessionId, string folder, string? path) => null;

        public IReadOnlyList<TerminalEvent> EventsOf(string fleetSessionId, HarnessMessage message)
            => [new TerminalEvent("message.updated", "{}"), new TerminalEvent("message.part.updated", "{}")];
    }
}
