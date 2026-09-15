using System.Text.Json;
using WeaveFleet.Application;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class AutomationDraftServiceTests
{
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly InMemoryMessageRepository _messages = new();
    private readonly AutomationDraftService _sut;

    public AutomationDraftServiceTests()
    {
        _sut = new AutomationDraftService(_sessions, _workspaces, _messages);
    }

    private void SeedSession(Workspace workspace)
    {
        _workspaces.Seed(workspace);
        _sessions.Seed(new Session { Id = "session-1", WorkspaceId = workspace.Id, Directory = workspace.Directory });
    }

    private void SeedMessage(string id, string role, string timestamp, params MessagePart[] parts) => _messages.Seed(new PersistedMessage
    {
        Id = id,
        SessionId = "session-1",
        Role = role,
        Timestamp = timestamp,
        PartsJson = JsonSerializer.Serialize(parts.ToList(), ApplicationJsonContext.Default.ListMessagePart),
    });

    [Fact]
    public async Task A_worktree_session_gives_its_first_message_and_its_repository()
    {
        SeedSession(new Workspace
        {
            Id = "ws-1",
            Directory = "/home/me/source/weave-fleet-worktrees/fleet-pr-digest",
            SourceDirectory = "/home/me/source/weave-fleet",
            IsolationStrategy = "worktree",
        });
        SeedMessage("m2", "user", "2026-09-14T09:05:00Z", new TextPart("Thanks, now the drafts too."));
        SeedMessage("m0", "assistant", "2026-09-14T09:00:30Z", new TextPart("Four PRs are open."));
        SeedMessage("m1", "user", "2026-09-14T09:00:00Z", new TextPart("Summarise the open PRs in weave-fleet."));

        var draft = (await _sut.FromSessionAsync("session-1")).Value;

        draft.ShouldBe(new AutomationDraft("Summarise the open PRs in weave-fleet.", "/home/me/source/weave-fleet", "worktree"));
    }

    [Fact]
    public async Task A_session_in_a_checkout_gives_that_folder()
    {
        SeedSession(new Workspace { Id = "ws-1", Directory = "/home/me/source/t3code", IsolationStrategy = "existing" });
        SeedMessage("m1", "user", "2026-09-14T09:00:00Z", new TextPart("Prune merged worktrees."));

        var draft = (await _sut.FromSessionAsync("session-1")).Value;

        draft.ShouldBe(new AutomationDraft("Prune merged worktrees.", "/home/me/source/t3code", "existing"));
    }

    [Fact]
    public async Task An_event_runs_session_gives_what_was_asked_without_the_event_context()
    {
        SeedSession(new Workspace { Id = "ws-1", Directory = "/home/me/source/t3code", IsolationStrategy = "existing" });
        SeedMessage("m1", "user", "2026-09-14T09:00:00Z", new TextPart("[Context]\nsession_created: A session started: Fix the build\n\n[Instruction]\nReview the new session's plan."));

        (await _sut.FromSessionAsync("session-1")).Value.Prompt.ShouldBe("Review the new session's plan.");
    }

    [Fact]
    public async Task A_quick_chat_has_no_folder()
    {
        SeedSession(new Workspace
        {
            Id = "ws-1",
            Directory = "/home/me/.weave-fleet/quick-chats/abc",
            IsolationStrategy = "existing",
            SourceProviderId = "builtin.quickchat",
        });
        SeedMessage("m1", "user", "2026-09-14T09:00:00Z", new TextPart("What's new in .NET 10?"));

        (await _sut.FromSessionAsync("session-1")).Value.Folder.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_session_is_not_found()
    {
        (await _sut.FromSessionAsync("nope")).IsFailure.ShouldBeTrue();
    }
}
