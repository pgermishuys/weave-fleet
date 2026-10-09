using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Sessions;

/// <summary>
/// A message reaches the receiving session as a prompt wrapped to say who sent it, and the link is a
/// <c>session.messaged</c> event. A sender on another machine is named with its machine.
/// </summary>
public sealed class SessionMessageDeliveryTests : IAsyncDisposable, IDisposable
{
    private const string UserId = "owner-1";

    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harnessSession = new("inst-1");
    private readonly SessionOrchestrator _orchestrator;
    private readonly SessionMessageDelivery _sut;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-message-delivery-{Guid.NewGuid():N}");

    public SessionMessageDeliveryTests()
    {
        Directory.CreateDirectory(_directory);
        _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext(UserId));
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _builder.RegisterHarness("opencode", "OpenCode").DefaultSession = _harnessSession;
        _orchestrator = _builder.Build();
        _sut = new SessionMessageDelivery(_builder.Prompting, _builder.EventBroadcaster);
    }

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task a_message_from_a_session_here_names_the_session()
    {
        var to = await CreateSessionAsync();

        var sent = await _sut.DeliverAsync("s-sender", "Fix login flake", fromMachine: null, to, "  Check the installer.  ", UserId, CancellationToken.None);

        sent.IsSuccess.ShouldBeTrue(sent.IsFailure ? sent.Error.Description : null);
        _harnessSession.SendPromptCalls.ShouldHaveSingleItem().Text
            .ShouldBe("<fleet-session-message from=\"s-sender\" title=\"Fix login flake\">\nCheck the installer.\n</fleet-session-message>");
        var messaged = Messaged(to);
        messaged.FromSessionId.ShouldBe("s-sender");
        messaged.FromMachineId.ShouldBeNull();
    }

    [Fact]
    public async Task a_message_from_another_machine_names_the_machine_too()
    {
        var to = await CreateSessionAsync();

        var sent = await _sut.DeliverAsync(
            "s-sender", "Fix \"login\" flake", new SessionMessageMachine("m-atlas", "atlas & co"), to, "Check the installer.", UserId, CancellationToken.None);

        sent.IsSuccess.ShouldBeTrue(sent.IsFailure ? sent.Error.Description : null);
        _harnessSession.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe(
            "<fleet-session-message from=\"s-sender\" title=\"Fix &quot;login&quot; flake\" machine=\"m-atlas\" machine-name=\"atlas &amp; co\">\n"
            + "Check the installer.\n</fleet-session-message>");
        var messaged = Messaged(to);
        messaged.FromSessionId.ShouldBe("s-sender");
        messaged.FromMachineId.ShouldBe("m-atlas");
        messaged.ToSessionId.ShouldBe(to);
    }

    [Fact]
    public async Task nothing_is_announced_when_the_session_cannot_be_prompted()
    {
        var sent = await _sut.DeliverAsync("s-sender", "Fix login flake", new SessionMessageMachine("m-atlas", "atlas"), "s-missing", "Hi.", UserId, CancellationToken.None);

        sent.IsFailure.ShouldBeTrue();
        _builder.EventBroadcaster.Broadcasts.ShouldNotContain(b => b.Type == "session.messaged");
    }

    private async Task<string> CreateSessionAsync()
    {
        var created = await _orchestrator.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, Title = "Check the Windows installer" });
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value.Session.Id;
    }

    private SessionMessagedPayload Messaged(string to)
    {
        var broadcast = _builder.EventBroadcaster.Broadcasts.Where(b => b.Type == "session.messaged").ShouldHaveSingleItem();
        broadcast.Topic.ShouldBe($"session:{to}");
        broadcast.UserId.ShouldBe(UserId);
        return broadcast.DomainEvent.ShouldBeOfType<SessionMessaged>().Payload;
    }
}
