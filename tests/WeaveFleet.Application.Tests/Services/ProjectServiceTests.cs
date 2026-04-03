using NSubstitute;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class ProjectServiceTests
{
    private readonly IProjectRepository _projectRepo = Substitute.For<IProjectRepository>();
    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();
    private readonly ProjectService _sut;

    public ProjectServiceTests()
    {
        _sut = new ProjectService(_projectRepo, _sessionRepo);
    }

    [Fact]
    public async Task CreateProjectAsync_AssignsNextPosition()
    {
        _projectRepo.ListAsync().Returns(new List<Project>
        {
            new() { Id = "p1", Name = "Existing", Position = 5, Type = "user", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" }
        });
        _projectRepo.InsertAsync(Arg.Any<Project>()).Returns(Task.CompletedTask);

        var result = await _sut.CreateProjectAsync("New Project");

        Assert.True(result.IsSuccess);
        Assert.Equal("New Project", result.Value.Name);
        Assert.Equal(6, result.Value.Position); // 5 + 1
        await _projectRepo.Received(1).InsertAsync(Arg.Is<Project>(p => p.Position == 6));
    }

    [Fact]
    public async Task GetProjectAsync_WhenNotFound_ReturnsFailure()
    {
        _projectRepo.GetByIdAsync("missing").Returns((Project?)null);

        var result = await _sut.GetProjectAsync("missing");

        Assert.True(result.IsFailure);
        Assert.Contains("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task DeleteProjectAsync_ScratchProject_ReturnsValidationError()
    {
        _projectRepo.GetByIdAsync("scratch-id").Returns(new Project
        {
            Id = "scratch-id", Name = "Scratch", Type = "scratch", Position = 0,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var result = await _sut.DeleteProjectAsync("scratch-id", "move_to_scratch");

        Assert.True(result.IsFailure);
        Assert.StartsWith("Validation.", result.Error.Code);
    }

    [Fact]
    public async Task DeleteProjectAsync_InvalidMode_ReturnsValidationError()
    {
        _projectRepo.GetByIdAsync("p1").Returns(new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var result = await _sut.DeleteProjectAsync("p1", "invalid-mode");

        Assert.True(result.IsFailure);
        Assert.StartsWith("Validation.", result.Error.Code);
    }

    [Fact]
    public async Task DeleteProjectAsync_DeleteSessions_DeletesSessionsThenProject()
    {
        var project = new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        };
        _projectRepo.GetByIdAsync("p1").Returns(project);
        _sessionRepo.DeleteByProjectIdAsync("p1").Returns(Task.CompletedTask);
        _projectRepo.DeleteAsync("p1").Returns(Task.FromResult(true));

        var result = await _sut.DeleteProjectAsync("p1", "delete_sessions");

        Assert.True(result.IsSuccess);
        await _sessionRepo.Received(1).DeleteByProjectIdAsync("p1");
        await _projectRepo.Received(1).DeleteAsync("p1");
    }

    [Fact]
    public async Task EnsureScratchProjectAsync_CreatesWhenAbsent()
    {
        _projectRepo.GetScratchProjectAsync().Returns((Project?)null);
        _projectRepo.InsertAsync(Arg.Any<Project>()).Returns(Task.CompletedTask);

        var result = await _sut.EnsureScratchProjectAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("scratch", result.Value.Type);
        await _projectRepo.Received(1).InsertAsync(Arg.Is<Project>(p => p.Type == "scratch"));
    }

    [Fact]
    public async Task EnsureScratchProjectAsync_ReturnsExistingWhenPresent()
    {
        var existing = new Project { Id = "s1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" };
        _projectRepo.GetScratchProjectAsync().Returns(existing);

        var result = await _sut.EnsureScratchProjectAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("s1", result.Value.Id);
        await _projectRepo.DidNotReceive().InsertAsync(Arg.Any<Project>());
    }
}
