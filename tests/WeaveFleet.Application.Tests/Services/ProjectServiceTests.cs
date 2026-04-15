using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class ProjectServiceTests
{
    private readonly InMemoryProjectRepository _projectRepo = new();
    private readonly InMemorySessionRepository _sessionRepo = new();
    private readonly IUserContext _userContext = new TestUserContext();
    private readonly ProjectService _sut;

    public ProjectServiceTests()
    {
        _sut = new ProjectService(_projectRepo, _sessionRepo, _userContext);
    }

    [Fact]
    public async Task CreateProjectAsync_AssignsNextPosition()
    {
        _projectRepo.Seed(new Project { Id = "p1", Name = "Existing", Position = 5, Type = "user", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" });

        var result = await _sut.CreateProjectAsync("New Project");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("New Project");
        result.Value.Position.ShouldBe(6); // 5 + 1
        result.Value.UserId.ShouldBe(TestUserContext.DefaultUserId);
        _projectRepo.All.Count(p => p.Position == 6).ShouldBe(1);
    }

    [Fact]
    public async Task GetProjectAsync_WhenNotFound_ReturnsFailure()
    {
        var result = await _sut.GetProjectAsync("missing");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("NotFound");
    }

    [Fact]
    public async Task DeleteProjectAsync_ScratchProject_ReturnsValidationError()
    {
        _projectRepo.Seed(new Project
        {
            Id = "scratch-id", Name = "Scratch", Type = "scratch", Position = 0,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var result = await _sut.DeleteProjectAsync("scratch-id", "move_to_scratch");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
    }

    [Fact]
    public async Task DeleteProjectAsync_InvalidMode_ReturnsValidationError()
    {
        _projectRepo.Seed(new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var result = await _sut.DeleteProjectAsync("p1", "invalid-mode");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
    }

    [Fact]
    public async Task DeleteProjectAsync_DeleteSessions_DeletesSessionsThenProject()
    {
        _projectRepo.Seed(new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });
        _sessionRepo.Seed(new Session { Id = "s1", ProjectId = "p1", Status = "active", CreatedAt = "2026-01-01" });

        var result = await _sut.DeleteProjectAsync("p1", "delete_sessions");

        result.IsSuccess.ShouldBeTrue();
        _sessionRepo.All.ShouldBeEmpty();
        _projectRepo.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureScratchProjectAsync_CreatesWhenAbsent()
    {
        var result = await _sut.EnsureScratchProjectAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe("scratch");
        result.Value.UserId.ShouldBe(TestUserContext.DefaultUserId);
        _projectRepo.All.Count(p => p.Type == "scratch").ShouldBe(1);
    }

    [Fact]
    public async Task EnsureScratchProjectAsync_ReturnsExistingWhenPresent()
    {
        var existing = new Project { Id = "s1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" };
        _projectRepo.Seed(existing);

        var result = await _sut.EnsureScratchProjectAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe("s1");
        _projectRepo.All.Count.ShouldBe(1); // No new insert
    }
}
