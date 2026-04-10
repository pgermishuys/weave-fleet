using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

internal static class RepositoryOwnershipTestHelper
{
    public static async Task<(Workspace Workspace, Instance Instance, Session Session)> SeedOwnedSessionGraphAsync(
        IDbConnectionFactory factory,
        string userId,
        string? sessionId = null,
        string? workspaceId = null,
        string? instanceId = null,
        string? projectId = null,
        string directory = "/tmp/ws")
    {
        var userContext = new TestUserContext(userId);
        var workspaceRepository = new DapperWorkspaceRepository(factory, userContext);
        var instanceRepository = new DapperInstanceRepository(factory, userContext);
        var sessionRepository = new DapperSessionRepository(factory, userContext);

        if (projectId is not null)
        {
            var projectRepository = new DapperProjectRepository(factory, userContext);
            await projectRepository.InsertAsync(new Project
            {
                Id = projectId,
                Name = $"Project-{userId}",
                Type = "user",
                Position = 0,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UpdatedAt = DateTime.UtcNow.ToString("O"),
                UserId = userId
            });
        }

        var workspace = new Workspace
        {
            Id = workspaceId ?? Guid.NewGuid().ToString(),
            Directory = directory,
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userId
        };

        var instance = new Instance
        {
            Id = instanceId ?? Guid.NewGuid().ToString(),
            Port = 9000,
            Directory = directory,
            Url = $"http://localhost/{userId}",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userId
        };

        var session = new Session
        {
            Id = sessionId ?? Guid.NewGuid().ToString(),
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            ProjectId = projectId,
            OpencodeSessionId = $"oc-{Guid.NewGuid():N}",
            Title = $"Session-{userId}",
            Status = "active",
            Directory = directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userId
        };

        await workspaceRepository.InsertAsync(workspace);
        await instanceRepository.InsertAsync(instance);
        await sessionRepository.InsertAsync(session);

        return (workspace, instance, session);
    }
}
