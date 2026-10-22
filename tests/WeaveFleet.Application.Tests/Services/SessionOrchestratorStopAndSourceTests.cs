using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Stop (<see cref="SessionOrchestrator.AbortSessionAsync"/>) and adding a source's context to a session
/// (<see cref="SessionOrchestrator.AddSourceToSessionAsync"/>, <see cref="SessionOrchestrator.PreviewAddSourceToSessionAsync"/>).
/// </summary>
public sealed class SessionOrchestratorStopAndSourceTests : IAsyncDisposable
{
    private const string SessionId = "sess-stop";
    private static readonly ResolvedSessionSource ReleaseNotes = new(
        SessionSourceCatalog.ExternalDocumentAddToSession,
        new ResolvedSessionInput(
            WorkspaceIntent: null,
            new ContextEnvelope("Release notes 2.4", "Rate limits now send Retry-After.", false, 33),
            new ProvenanceRecord(
                "provider.external",
                SessionSourceTypeNames.ExternalDocument,
                SessionSourceActions.AddToSession,
                "doc-24",
                "https://docs.example.com/release-notes/2.4",
                "Release notes 2.4",
                "What changed in 2.4",
                "2026-01-01T00:00:00Z")));

    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _running = new("inst-stop");

    public SessionOrchestratorStopAndSourceTests()
    {
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        _builder.WithSessionSourceProvider(new StubSourceProvider(ReleaseNotes));
    }

    public ValueTask DisposeAsync() => _running.DisposeAsync();

    private SessionOrchestrator Build(string retention = "active", bool running = true)
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            WorkspaceId = "ws-stop",
            InstanceId = "inst-stop",
            Title = "Rate limit headers",
            Status = "active",
            Directory = "/tmp/fleet-stop",
            CreatedAt = "2026-01-01",
            RetentionStatus = retention,
            HarnessType = "opencode",
            RuntimeMode = "manual",
            UserId = "user-1",
        });
        if (running)
            _builder.InstanceTracker.Register("inst-stop", _running);
        return _builder.Build();
    }

    private static SessionSourceSelection ReleaseNotesSelection() => new()
    {
        Key = SessionSourceCatalog.ExternalDocumentAddToSession.Key,
        Input = System.Text.Json.JsonSerializer.SerializeToElement(new { resourceId = "doc-24" }),
    };

    [Fact]
    public async Task stop_aborts_the_running_turn()
    {
        var sut = Build();

        var result = await sut.AbortSessionAsync(SessionId);

        result.IsSuccess.ShouldBeTrue();
        _running.AbortCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task stop_tells_a_session_waiting_on_the_turn_that_it_was_stopped_not_finished()
    {
        var sender = new RecordingSender();
        var updates = new SessionUpdates(
            _builder.ActivityTracker,
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionUpdateSender>(sender);
            }),
            NullLogger<SessionUpdates>.Instance);
        _builder.WithSessionUpdates(updates);
        var sut = Build();
        updates.Watch(new SessionUpdateWatch("s-asker", SessionId, "user-1", "msg-1"));

        updates.Observe(SessionId, new MessageUpdated
        {
            Payload = new MessageLifecyclePayload
            {
                Info = new MessageEventInfo { Id = "reply-1", Role = "assistant", SessionId = SessionId, ParentId = "msg-1", Time = new MessageEventTime { Created = 0 } },
            },
        });
        (await sut.AbortSessionAsync(SessionId)).IsSuccess.ShouldBeTrue();
        updates.Observe(SessionId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = SessionId } });
        await updates.Pending;

        sender.Sent.ShouldHaveSingleItem().Outcome.ShouldBe(SessionUpdates.Stopped);
    }

    [Fact]
    public async Task stop_on_an_archived_session_is_refused()
    {
        var sut = Build(retention: "archived");

        var result = await sut.AbortSessionAsync(SessionId);

        result.Error.Description.ShouldBe("Archived sessions are read-only.");
        _running.AbortCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task stop_on_a_missing_session_is_not_found()
    {
        var sut = Build();

        (await sut.AbortSessionAsync("sess-gone")).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task a_previewed_source_gives_its_context_without_prompting()
    {
        var sut = Build();

        var result = await sut.PreviewAddSourceToSessionAsync(SessionId, ReleaseNotesSelection());

        result.Value.ShouldBe(ReleaseNotes.Input.ContextEnvelope);
        _running.SendPromptCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_added_source_goes_to_the_agent_as_a_prompt_and_is_recorded()
    {
        var sut = Build();

        var result = await sut.AddSourceToSessionAsync(SessionId, ReleaseNotesSelection(), confirm: true);

        result.IsSuccess.ShouldBeTrue();
        _running.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe("[Source: Release notes 2.4]\n\nRate limits now send Retry-After.");
        var usage = _builder.SessionSourceUsageRepository.All.ShouldHaveSingleItem();
        usage.SessionId.ShouldBe(SessionId);
        usage.WorkspaceId.ShouldBe("ws-stop");
        usage.ProviderId.ShouldBe("provider.external");
        usage.ResourceId.ShouldBe("doc-24");
        usage.ResourceUrl.ShouldBe("https://docs.example.com/release-notes/2.4");
        usage.Title.ShouldBe("Release notes 2.4");
    }

    [Fact]
    public async Task a_source_whose_prompt_fails_is_not_recorded()
    {
        // Asleep, and its workspace is gone, so it can't wake to take the prompt.
        var sut = Build(running: false);

        var result = await sut.AddSourceToSessionAsync(SessionId, ReleaseNotesSelection(), confirm: true);

        result.IsFailure.ShouldBeTrue();
        _builder.SessionSourceUsageRepository.All.ShouldBeEmpty();
    }

    private sealed class StubSourceProvider(ResolvedSessionSource resolved) : ISessionSourceProvider
    {
        public string ProviderId => resolved.Descriptor.Key.ProviderId;

        public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() => [resolved.Descriptor];

        public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
            => Task.FromResult<Result<ResolvedSessionSource>>(resolved);
    }
}

file sealed class RecordingSender : ISessionUpdateSender
{
    public List<SessionUpdate> Sent { get; } = [];

    public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
        => Task.FromResult<SessionUpdate?>(new SessionUpdate(watch.AskerId, watch.TargetId, watch.UserId, "…", SessionUpdates.OutcomeOf(failure)));

    public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct)
    {
        Sent.Add(update);
        return Task.FromResult(true);
    }
}

file sealed class NoUserScope : IBackgroundUserScope
{
    public IDisposable Begin(string userId) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
