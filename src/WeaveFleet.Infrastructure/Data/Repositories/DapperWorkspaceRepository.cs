using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperWorkspaceRepository(IDbConnectionFactory connectionFactory) : IWorkspaceRepository
{
    public async Task InsertAsync(Workspace workspace)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO workspaces (
                id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary, source_resolved_at)
            VALUES (
                @Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName,
                @SourceProviderId, @SourceType, @SourceResourceId, @SourceResourceUrl, @SourceTitle, @SourceSummary, @SourceResolvedAt)
            """, workspace);
    }

    public async Task<Workspace?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Workspace>(
            "SELECT * FROM workspaces WHERE id = @Id", new { Id = id });
    }

    public async Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Workspace>(
            "SELECT * FROM workspaces WHERE directory = @Directory AND isolation_strategy = @IsolationStrategy",
            new { Directory = directory, IsolationStrategy = isolationStrategy });
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Workspace>(
            "SELECT * FROM workspaces ORDER BY created_at DESC");
        return results.AsList();
    }

    public async Task MarkCleanedAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE workspaces SET cleaned_up_at = datetime('now') WHERE id = @Id",
            new { Id = id });
    }

    public async Task UpdateDisplayNameAsync(string id, string displayName)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE workspaces SET display_name = @DisplayName WHERE id = @Id",
            new { Id = id, DisplayName = displayName });
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
            WHERE id = @Id
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
                ResolvedAt = resolvedAt
            });
    }
}
