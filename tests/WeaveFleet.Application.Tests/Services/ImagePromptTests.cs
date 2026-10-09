using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Images go with a prompt only to a harness that passes them on; another would drop them without a word.</summary>
public sealed class ImagePromptTests : IAsyncDisposable
{
    private const string SessionId = "s1";
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harnessSession = new("inst-1");

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    private SessionOrchestrator Build(bool supportsImages)
    {
        _builder.RegisterHarness("pictures", capabilities: new HarnessCapabilities { SupportsImageAttachments = supportsImages });
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active",
            HarnessType = "pictures",
        });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _builder.InstanceTracker.Register("inst-1", _harnessSession);
        return _builder.Build();
    }

    private static PromptOptions WithImage => new() { Attachments = [new HarnessAttachment("image/png", "red.png", "iVBORw0KGgo=")] };

    [Fact]
    public async Task passes_the_images_to_a_harness_that_takes_them()
    {
        var sut = Build(supportsImages: true);

        var result = await sut.PromptSessionAsync(SessionId, "what colour is this?", WithImage);

        result.IsSuccess.ShouldBeTrue();
        _harnessSession.SendPromptCalls.Single().Options!.Attachments.ShouldHaveSingleItem().Filename.ShouldBe("red.png");
    }

    [Fact]
    public async Task refuses_images_a_harness_would_drop()
    {
        var sut = Build(supportsImages: false);

        var result = await sut.PromptSessionAsync(SessionId, "what colour is this?", WithImage);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Prompt.Attachments");
        _harnessSession.SendPromptCalls.ShouldBeEmpty();
        _builder.EventBroadcaster.Broadcasts.ShouldBeEmpty();
    }
}
