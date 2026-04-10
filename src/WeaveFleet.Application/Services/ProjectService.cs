using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Encapsulates business logic for project management.
/// </summary>
public sealed class ProjectService(
    IProjectRepository projectRepository,
    ISessionRepository sessionRepository,
    IUserContext userContext)
{
    public async Task<Result<Project>> CreateProjectAsync(string name, string? description = null)
    {
        var all = await projectRepository.ListAsync();
        var nextPosition = all.Count > 0 ? all.Max(p => p.Position) + 1 : 1;

        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            Type = "user",
            Position = nextPosition,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await projectRepository.InsertAsync(project);
        return project;
    }

    public async Task<Result<Project>> GetProjectAsync(string id)
    {
        var project = await projectRepository.GetByIdAsync(id);
        if (project is null)
            return FleetError.NotFoundFor(nameof(Project), id);
        return project;
    }

    public async Task<Result<IReadOnlyList<Project>>> ListProjectsAsync()
    {
        var projects = await projectRepository.ListAsync();
        return Result.Success(projects);
    }

    public async Task<Result<Project>> UpdateProjectAsync(string id, string? name, string? description)
    {
        var project = await projectRepository.GetByIdAsync(id);
        if (project is null)
            return FleetError.NotFoundFor(nameof(Project), id);

        project.Name = name ?? project.Name;
        project.Description = description ?? project.Description;
        project.UpdatedAt = DateTime.UtcNow.ToString("O");

        await projectRepository.UpdateAsync(project);
        return project;
    }

    public async Task<Result<Unit>> DeleteProjectAsync(string id, string mode)
    {
        var project = await projectRepository.GetByIdAsync(id);
        if (project is null)
            return FleetError.NotFoundFor(nameof(Project), id);

        if (project.Type == "scratch")
            return FleetError.ValidationError("Id", "Cannot delete the scratch project.");

        if (mode == "move_to_scratch")
        {
            var scratch = await projectRepository.GetScratchProjectAsync();
            if (scratch is null)
                return FleetError.ValidationError("ScratchProject", "No scratch project exists to move sessions to.");

            await projectRepository.MoveSessionsToProjectAsync(id, scratch.Id);
        }
        else if (mode == "delete_sessions")
        {
            await sessionRepository.DeleteByProjectIdAsync(id);
        }
        else
        {
            return FleetError.ValidationError("Mode", $"Invalid delete mode: '{mode}'. Must be 'move_to_scratch' or 'delete_sessions'.");
        }

        await projectRepository.DeleteAsync(id);
        return Unit.Value;
    }

    public async Task<Result<Unit>> ReorderProjectAsync(string id, int newPosition)
    {
        var project = await projectRepository.GetByIdAsync(id);
        if (project is null)
            return FleetError.NotFoundFor(nameof(Project), id);

        await projectRepository.ReorderAsync(id, newPosition);
        return Unit.Value;
    }

    public async Task<Result<Project>> EnsureScratchProjectAsync()
    {
        var scratch = await projectRepository.GetScratchProjectAsync();
        if (scratch is not null)
            return scratch;

        scratch = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Scratch",
            Description = "Default project for uncategorized sessions",
            Type = "scratch",
            Position = 0,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
        };

        await projectRepository.InsertAsync(scratch);
        return scratch;
    }
}
