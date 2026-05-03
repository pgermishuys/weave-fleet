using System.Data.Common;
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
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", workspace.Id);
                cmd.AddParameter("Directory", workspace.Directory);
                cmd.AddParameter("SourceDirectory", workspace.SourceDirectory);
                cmd.AddParameter("IsolationStrategy", workspace.IsolationStrategy);
                cmd.AddParameter("Branch", workspace.Branch);
                cmd.AddParameter("CreatedAt", workspace.CreatedAt);
                cmd.AddParameter("CleanedUpAt", workspace.CleanedUpAt);
                cmd.AddParameter("DisplayName", workspace.DisplayName);
                cmd.AddParameter("SourceProviderId", workspace.SourceProviderId);
                cmd.AddParameter("SourceType", workspace.SourceType);
                cmd.AddParameter("SourceResourceId", workspace.SourceResourceId);
                cmd.AddParameter("SourceResourceUrl", workspace.SourceResourceUrl);
                cmd.AddParameter("SourceTitle", workspace.SourceTitle);
                cmd.AddParameter("SourceSummary", workspace.SourceSummary);
                cmd.AddParameter("SourceResolvedAt", workspace.SourceResolvedAt);
                cmd.AddParameter("UserId", insertUserId);
            });
    }

    public async Task<Workspace?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM workspaces WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadWorkspace);
    }

    public async Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM workspaces WHERE directory = @Directory AND isolation_strategy = @IsolationStrategy AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Directory", directory);
                cmd.AddParameter("IsolationStrategy", isolationStrategy);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadWorkspace);
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM workspaces WHERE user_id = @UserId ORDER BY created_at DESC",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            ReadWorkspace);
    }

    public async Task MarkCleanedAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE workspaces SET cleaned_up_at = datetime('now') WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateDisplayNameAsync(string id, string displayName)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE workspaces SET display_name = @DisplayName WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("DisplayName", displayName);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateSourceMetadataAsync(string id, string providerId, string sourceType, string? resourceId, string? resourceUrl, string? title, string? summary, string? resolvedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ProviderId", providerId);
                cmd.AddParameter("SourceType", sourceType);
                cmd.AddParameter("ResourceId", resourceId);
                cmd.AddParameter("ResourceUrl", resourceUrl);
                cmd.AddParameter("Title", title);
                cmd.AddParameter("Summary", summary);
                cmd.AddParameter("ResolvedAt", resolvedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    private static Workspace ReadWorkspace(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Directory = r.GetString(r.GetOrdinal("directory")),
        SourceDirectory = r.GetNullableString(r.GetOrdinal("source_directory")),
        IsolationStrategy = r.GetString(r.GetOrdinal("isolation_strategy")),
        Branch = r.GetNullableString(r.GetOrdinal("branch")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        CleanedUpAt = r.GetNullableString(r.GetOrdinal("cleaned_up_at")),
        DisplayName = r.GetNullableString(r.GetOrdinal("display_name")),
        SourceProviderId = r.GetNullableString(r.GetOrdinal("source_provider_id")),
        SourceType = r.GetNullableString(r.GetOrdinal("source_type")),
        SourceResourceId = r.GetNullableString(r.GetOrdinal("source_resource_id")),
        SourceResourceUrl = r.GetNullableString(r.GetOrdinal("source_resource_url")),
        SourceTitle = r.GetNullableString(r.GetOrdinal("source_title")),
        SourceSummary = r.GetNullableString(r.GetOrdinal("source_summary")),
        SourceResolvedAt = r.GetNullableString(r.GetOrdinal("source_resolved_at")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
    };
}
