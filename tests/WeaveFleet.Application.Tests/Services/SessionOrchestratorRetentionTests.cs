using Shouldly;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// What archiving, restoring and deleting a session clean up (<see cref="SessionOrchestrator.ArchiveSessionAsync"/>,
/// <see cref="SessionOrchestrator.UnarchiveSessionAsync"/>, <see cref="SessionOrchestrator.DeleteSessionAsync"/>).
/// </summary>
public sealed class SessionOrchestratorRetentionTests : IAsyncDisposable
{
    private const string SessionId = "sess-retain";
    private readonly Cleanup _cleanup = new();
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _running = new("inst-retain");

    public SessionOrchestratorRetentionTests()
    {
        _builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"))
            .WithSessionTerminals(_cleanup)
            .WithSessionApps(_cleanup)
            .WithSessionPages(_cleanup);
        _builder.RegisterHarness("opencode", "OpenCode");
        _builder.WorkspaceRepository.Seed(new Workspace
        {
            Id = "ws-retain", Directory = "/tmp/fleet-retain", IsolationStrategy = "existing", CreatedAt = "2026-01-01", UserId = "user-1",
        });
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            WorkspaceId = "ws-retain",
            InstanceId = "inst-retain",
            Title = "Rate limit headers",
            Status = "active",
            Directory = "/tmp/fleet-retain",
            CreatedAt = "2026-01-01T00:00:00.0000000Z",
            RetentionStatus = "active",
            HarnessType = "opencode",
            UserId = "user-1",
        });
        _builder.InstanceRepository.Seed(new Instance
        {
            Id = "inst-retain", Directory = "/tmp/fleet-retain", Url = string.Empty, Status = "running", CreatedAt = "2026-01-01", UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-retain", _running);
    }

    public ValueTask DisposeAsync() => _running.DisposeAsync();

    [Fact]
    public async Task archiving_ends_the_terminals_and_apps_and_what_the_agent_left_running_but_keeps_the_harness()
    {
        var sut = _builder.Build();

        var result = await sut.ArchiveSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue();
        _cleanup.Calls.ShouldBe([$"terminals:{SessionId}", $"apps:{SessionId}"]);
        _running.ArchiveCalled.ShouldBeTrue();
        _running.DeleteCalled.ShouldBeFalse();
        _builder.InstanceTracker.Get("inst-retain").ShouldBeSameAs(_running);
        (await _builder.SessionRepository.GetByIdAsync(SessionId))!.RetentionStatus.ShouldBe("archived");
    }

    [Fact]
    public async Task archiving_an_archived_session_does_nothing_more()
    {
        var sut = _builder.Build();
        await sut.ArchiveSessionAsync(SessionId);
        var broadcasts = _builder.EventBroadcaster.Broadcasts.Count;
        _cleanup.Calls.Clear();

        (await sut.ArchiveSessionAsync(SessionId)).IsSuccess.ShouldBeTrue();

        _builder.EventBroadcaster.Broadcasts.Count.ShouldBe(broadcasts);
        _cleanup.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task restoring_brings_it_back_without_starting_anything()
    {
        var sut = _builder.Build();
        await sut.ArchiveSessionAsync(SessionId);
        _cleanup.Calls.Clear();

        (await sut.UnarchiveSessionAsync(SessionId)).IsSuccess.ShouldBeTrue();

        (await _builder.SessionRepository.GetByIdAsync(SessionId))!.RetentionStatus.ShouldBe("active");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == "session_unarchived");
        _cleanup.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task deleting_ends_the_harness_terminals_and_apps_then_the_session_and_its_pages()
    {
        var sut = _builder.Build();

        var result = await sut.DeleteSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue();
        _running.DeleteCalled.ShouldBeTrue();
        _builder.InstanceTracker.Get("inst-retain").ShouldBeNull();
        _cleanup.Calls.ShouldBe([$"terminals:{SessionId}", $"apps:{SessionId}", $"pages:{SessionId}"]);
        (await _builder.SessionRepository.GetByIdAsync(SessionId)).ShouldBeNull();
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == "session_deleted");
        _builder.AnalyticsCollector.SessionSnapshots.ShouldContain(s => s.SessionId == SessionId && s.Status == "deleted");
    }

    [Fact]
    public async Task archiving_restoring_or_deleting_a_missing_session_is_not_found()
    {
        var sut = _builder.Build();

        (await sut.ArchiveSessionAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
        (await sut.UnarchiveSessionAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
        (await sut.DeleteSessionAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
        _cleanup.Calls.ShouldBeEmpty();
    }

    /// <summary>Records which of the session's terminals, apps and pages were cleaned up, in order.</summary>
    private sealed class Cleanup : ISessionTerminalCleanup, ISessionAppCleanup, IPageStore
    {
        public List<string> Calls { get; } = [];

        public Task EndSessionAsync(string sessionId, CancellationToken ct = default)
        {
            Calls.Add($"terminals:{sessionId}");
            return Task.CompletedTask;
        }

        public Task StopSessionAppsAsync(string sessionId, CancellationToken ct = default)
        {
            Calls.Add($"apps:{sessionId}");
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
        {
            Calls.Add($"pages:{sessionId}");
            return Task.CompletedTask;
        }

        public Task<PageCopyResult> CopyAsync(string sessionId, string pageId, string entryFile, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string? Resolve(string pageId, string path) => null;

        public Task DeleteAsync(string sessionId, string pageId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
