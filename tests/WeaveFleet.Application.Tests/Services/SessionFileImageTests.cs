using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Images a file tab shows as a picture: <see cref="SessionOrchestrator.ResolveSessionImageAsync"/>.</summary>
public sealed class SessionFileImageTests : IDisposable
{
    private const string SessionId = "sess-images";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-images-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly SessionOrchestrator _sut;

    public SessionFileImageTests()
    {
        _directory = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(_directory, "docs"));
        var builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-images",
            Directory = _directory,
            LifecycleStatus = "running",
            RetentionStatus = "active",
            RuntimeMode = "manual",
            ActivityStatus = "idle",
            UserId = "user-1",
        });
        _sut = builder.Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("docs/map.png", "image/png")]
    [InlineData("docs/photo.JPG", "image/jpeg")]
    [InlineData("docs/logo.svg", "image/svg+xml")]
    [InlineData("docs\\anim.webp", "image/webp")]
    public async Task an_image_in_the_session_resolves_to_its_file_and_content_type(string path, string contentType)
    {
        var full = Path.Combine(_directory, path.Replace('\\', '/'));
        File.WriteAllBytes(full, new byte[600 * 1024]);

        var result = await _sut.ResolveSessionImageAsync(SessionId, path);

        result.IsSuccess.ShouldBeTrue();
        result.Value.FullPath.ShouldBe(Path.GetFullPath(full));
        result.Value.ContentType.ShouldBe(contentType);
    }

    [Fact]
    public async Task a_file_that_isnt_an_image_is_refused()
    {
        File.WriteAllText(Path.Combine(_directory, "docs", "notes.txt"), "notes");

        var result = await _sut.ResolveSessionImageAsync(SessionId, "docs/notes.txt");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Only images");
    }

    [Fact]
    public async Task a_missing_image_is_not_found()
    {
        var result = await _sut.ResolveSessionImageAsync(SessionId, "docs/gone.png");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("File.NotFound");
    }

    [Fact]
    public async Task an_image_outside_the_session_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), [1, 2, 3]);

        var result = await _sut.ResolveSessionImageAsync(SessionId, "../outside.png");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("traversal");
    }

    [Fact]
    public async Task a_symlink_to_an_image_outside_the_session_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(_root, "outside.png");
        File.WriteAllBytes(outside, [1, 2, 3]);
        File.CreateSymbolicLink(Path.Combine(_directory, "docs", "link.png"), outside);

        var result = await _sut.ResolveSessionImageAsync(SessionId, "docs/link.png");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside");
    }
}
