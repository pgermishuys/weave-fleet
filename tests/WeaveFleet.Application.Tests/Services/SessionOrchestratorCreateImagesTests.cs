using Shouldly;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Images that go with the first message: sent with it, or refused before anything is created.</summary>
public sealed class SessionOrchestratorCreateImagesTests : IDisposable
{
    private static readonly HarnessAttachment[] Screenshot = [new("image/png", "screenshot.png", "aGVsbG8=")];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-create-images-{Guid.NewGuid():N}");
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessRuntime _runtime;

    public SessionOrchestratorCreateImagesTests()
    {
        Directory.CreateDirectory(_directory);
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsImageAttachments = true });
        _builder.RegisterHarness("no-images", "No images", new HarnessCapabilities { SupportsImageAttachments = false });
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = "2026-01-01" });
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task a_harness_that_cannot_take_images_refuses_before_anything_is_created()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Look", HarnessType = "no-images", InitialPrompt = "What is this?", InitialAttachments = Screenshot,
        });

        result.Error.Code.ShouldBe("Validation.InitialAttachments");
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task images_without_a_first_message_are_refused()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Look", InitialAttachments = Screenshot,
        });

        result.Error.Code.ShouldBe("Validation.InitialAttachments");
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_first_message_goes_to_the_harness_with_its_images()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Look", InitialPrompt = "What is this?", InitialAttachments = Screenshot,
        });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        var call = ((FakeHarnessSession)_runtime.DefaultSession).SendPromptCalls.ShouldHaveSingleItem();
        call.Text.ShouldContain("What is this?");
        call.Options!.Attachments.ShouldHaveSingleItem().Filename.ShouldBe("screenshot.png");
    }
}
