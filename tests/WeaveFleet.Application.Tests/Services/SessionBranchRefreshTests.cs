using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>A session's stored branch follows a <c>git switch</c> made in its folder, read when a turn ends.</summary>
public sealed class SessionBranchRefreshTests : IDisposable
{
    private const string Session = "session-1";
    private const string Owner = "user-1";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fleet-branch-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly SessionContextRecorder _sut;

    public SessionBranchRefreshTests()
    {
        Directory.CreateDirectory(_folder);
        Git("init", "-q", "-b", "feature/one");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "first");
        _workspaces.Seed(new Workspace { Id = "ws-1", Directory = _folder, IsolationStrategy = "worktree", Branch = "feature/one", UserId = Owner });
        _sessions.InsertAsync(new Session { Id = Session, WorkspaceId = "ws-1", UserId = Owner }).GetAwaiter().GetResult();
        _sut = new SessionContextRecorder(
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<IUserContext>(new TestUserContext(Owner));
                services.AddSingleton<ISessionContextRepository>(new InMemorySessionContextRepository());
                services.AddSingleton<ISessionRepository>(_sessions);
                services.AddSingleton<IWorkspaceRepository>(_workspaces);
                services.AddSingleton<IEventBroadcaster>(_broadcaster);
                services.AddSingleton<GitDiffService>();
                services.AddScoped<SessionContextService>();
                services.AddScoped<SessionBranchService>();
            }),
            NullLogger<SessionContextRecorder>.Instance);
    }

    private void Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0);
    }

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = Session } };

    [Fact]
    public async Task a_switch_in_the_folder_is_stored_and_broadcast_when_the_turn_ends()
    {
        Git("switch", "-q", "-c", "feature/two");
        _sut.Observe(Session, Owner, Idled());
        await _sut.Idle(Session);

        (await _workspaces.GetByIdAsync("ws-1"))!.Branch.ShouldBe("feature/two");
        _broadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == SessionBranchService.BranchEventType);
    }

    [Fact]
    public async Task an_unchanged_branch_a_detached_head_and_a_missing_folder_change_nothing()
    {
        _sut.Observe(Session, Owner, Idled());
        await _sut.Idle(Session);
        _broadcaster.Broadcasts.ShouldNotContain(b => b.Type == SessionBranchService.BranchEventType);

        Git("checkout", "-q", "--detach");
        _sut.Observe(Session, Owner, Idled());
        await _sut.Idle(Session);
        (await _workspaces.GetByIdAsync("ws-1"))!.Branch.ShouldBe("feature/one");

        Directory.Delete(_folder, true);
        _sut.Observe(Session, Owner, Idled());
        await _sut.Idle(Session);
        (await _workspaces.GetByIdAsync("ws-1"))!.Branch.ShouldBe("feature/one");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
