using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperWorkspaceRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IWorkspaceRepository
{
    public async Task InsertAsync(Workspace workspace)
    {
        var insertUserId = string.IsNullOrWhiteSpace(workspace.UserId)
            ? userContext.UserId
            : workspace.UserId;

        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO workspaces (
                id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary,
                source_resolved_at, user_id)
            VALUES (
                @Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName,
                @SourceProviderId, @SourceType, @SourceResourceId, @SourceResourceUrl, @SourceTitle, @SourceSummary,
                @SourceResolvedAt, @UserId)
            """,
            new
            {
                workspace.Id,
                workspace.Directory,
                workspace.SourceDirectory,
                workspace.IsolationStrategy,
                workspace.Branch,
                workspace.CreatedAt,
                workspace.CleanedUpAt,
                workspace.DisplayName,
                workspace.SourceProviderId,
                workspace.SourceType,
                workspace.SourceResourceId,
                workspace.SourceResourceUrl,
                workspace.SourceTitle,
                workspace.SourceSummary,
                workspace.SourceResolvedAt,
                UserId = insertUserId
            });
    }

    public async Task<Workspace?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Workspace>(
            "SELECT * FROM workspaces WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }

    public async Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Workspace>(
            "SELECT * FROM workspaces WHERE directory = @Directory AND isolation_strategy = @IsolationStrategy AND user_id = @UserId",
            new { Directory = directory, IsolationStrategy = isolationStrategy, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Workspace>(
            "SELECT * FROM workspaces WHERE user_id = @UserId ORDER BY created_at DESC",
            new { UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task MarkCleanedAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE workspaces SET cleaned_up_at = datetime('now') WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }

    public async Task UpdateDisplayNameAsync(string id, string displayName)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE workspaces SET display_name = @DisplayName WHERE id = @Id AND user_id = @UserId",
            new { Id = id, DisplayName = displayName, UserId = userContext.UserId });
    }

    public async Task UpdateSourceMetadataAsync(string id, string providerId, string sourceType, string? resourceId, string? resourceUrl, string? title, string? summary, string? resolvedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE workspaces
            SET source_provider_id = @ProviderId,
                source_type = @SourceType,
                source_resource_id = @ResourceId,
                source_resource_url = @ResourceUrl,
                source_title = @Title,
                source_summary = @Summary,
                source_resolved_at = @ResolvedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            new
            {
                Id = id,
                ProviderId = providerId,
                SourceType = sourceType,
                ResourceId = resourceId,
                ResourceUrl = resourceUrl,
                Title = title,
                Summary = summary,
                ResolvedAt = resolvedAt,
                UserId = userContext.UserId
            });
    }
}
