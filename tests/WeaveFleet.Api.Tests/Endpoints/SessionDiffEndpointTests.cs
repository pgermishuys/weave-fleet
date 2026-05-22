using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionDiffEndpointTests
{
    [Fact]
    public async Task get_session_diffs_for_workspace_subdirectory_excludes_files_outside_workspace_prefix()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"weave-api-diff-prefix-{Guid.NewGuid():N}");
        var workspaceDirectory = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceDirectory);
        var service = new GitDiffService();

        try
        {
            await RunGitAsync(tempRoot, "init");
            await File.WriteAllTextAsync(Path.Combine(workspaceDirectory, "tracked.txt"), "alpha\nbeta\n");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "outside.txt"), "outside alpha\n");
            await RunGitAsync(tempRoot, "add", ".");
            await RunGitAsync(tempRoot, "-c", "user.name=Weave Test", "-c", "user.email=weave@example.invalid", "commit", "-m", "initial");

            var baseline = await service.CaptureBaselineAsync(workspaceDirectory, "api-prefix-check", CancellationToken.None);
            baseline.ShouldNotBeNull();

            await File.WriteAllTextAsync(Path.Combine(workspaceDirectory, "tracked.txt"), "alpha\nbeta changed\ngamma\n");
            await File.WriteAllTextAsync(Path.Combine(workspaceDirectory, "new.txt"), "inside new\n");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "outside.txt"), "outside changed\n");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "outside-new.txt"), "outside new\n");

            var persistedWorkspaceDirectory = Path.Combine(baseline.RepoRoot, "workspace");
            await using var factory = new ApiWebApplicationFactory(authEnabled: false);
            await InsertSessionAsync(factory, "session-prefix", "workspace-prefix", "instance-prefix", persistedWorkspaceDirectory, baseline.RefName, baseline.RepoRoot);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/sessions/session-prefix/diffs");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
            json.GetProperty("available").GetBoolean().ShouldBeTrue();
            var diffs = json.GetProperty("diffs").EnumerateArray().ToArray();
            var files = diffs.Select(diff => diff.GetProperty("file").GetString()).ToArray();
            files.ShouldBe(["workspace/new.txt", "workspace/tracked.txt"]);

            var newFile = diffs.Single(diff => diff.GetProperty("file").GetString() == "workspace/new.txt");
            newFile.GetProperty("before").GetString().ShouldBe(string.Empty);
            newFile.GetProperty("after").GetString().ShouldBe("inside new\n");
            newFile.GetProperty("isBinary").GetBoolean().ShouldBeFalse();
            newFile.GetProperty("isTruncated").GetBoolean().ShouldBeFalse();

            var trackedFile = diffs.Single(diff => diff.GetProperty("file").GetString() == "workspace/tracked.txt");
            trackedFile.GetProperty("before").GetString().ShouldBe("alpha\nbeta\n");
            trackedFile.GetProperty("after").GetString().ShouldBe("alpha\nbeta changed\ngamma\n");
            trackedFile.GetProperty("isBinary").GetBoolean().ShouldBeFalse();
            trackedFile.GetProperty("isTruncated").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_session_diffs_when_baseline_metadata_is_null_returns_unavailable_empty_response()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();
        var createdAt = DateTime.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = "workspace-null-baseline",
                Directory = "/tmp/workspace-null-baseline",
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = "Null Baseline Workspace",
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = "instance-null-baseline",
                Port = 0,
                Pid = (int?)null,
                Directory = "/tmp/workspace-null-baseline",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, git_baseline_ref, git_repo_root, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @GitBaselineRef, @GitRepoRoot, @UserId)",
            new
            {
                Id = "session-null-baseline",
                WorkspaceId = "workspace-null-baseline",
                InstanceId = "instance-null-baseline",
                ProjectId = (string?)null,
                OpencodeSessionId = "opencode-null-baseline",
                Title = "Null Baseline Session",
                Status = "active",
                Directory = "/tmp/workspace-null-baseline",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = (string?)null,
                ActivityStatus = "idle",
                LifecycleStatus = "running",
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = "opencode",
                HarnessResumeToken = (string?)null,
                IsHidden = false,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                GitBaselineRef = (string?)null,
                GitRepoRoot = (string?)null,
                UserId = "local-user"
            });

        var response = await client.GetAsync("/api/sessions/session-null-baseline/diffs");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("available").GetBoolean().ShouldBeFalse();
        json.GetProperty("diffs").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task get_session_diffs_when_session_directory_is_outside_repo_root_returns_unavailable_empty_response()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var repoRoot = Path.Combine(Path.GetTempPath(), $"weave-api-diff-repo-{Guid.NewGuid():N}");
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"weave-api-diff-outside-{Guid.NewGuid():N}");

        await InsertSessionAsync(
            factory,
            "session-outside-repo",
            "workspace-outside-repo",
            "instance-outside-repo",
            outsideDirectory,
            "refs/fleet/baselines/session-outside-repo",
            repoRoot);

        var response = await client.GetAsync("/api/sessions/session-outside-repo/diffs");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("available").GetBoolean().ShouldBeFalse();
        json.GetProperty("diffs").EnumerateArray().ShouldBeEmpty();
    }

    private static async Task InsertSessionAsync(
        ApiWebApplicationFactory factory,
        string sessionId,
        string workspaceId,
        string instanceId,
        string directory,
        string? gitBaselineRef,
        string? gitRepoRoot)
    {
        using var scope = factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();
        var createdAt = DateTime.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = workspaceId,
                Directory = directory,
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = "Diff Workspace",
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = instanceId,
                Port = 0,
                Pid = (int?)null,
                Directory = directory,
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, git_baseline_ref, git_repo_root, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @GitBaselineRef, @GitRepoRoot, @UserId)",
            new
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                ProjectId = (string?)null,
                OpencodeSessionId = $"opencode-{sessionId}",
                Title = "Diff Session",
                Status = "active",
                Directory = directory,
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = (string?)null,
                ActivityStatus = "idle",
                LifecycleStatus = "running",
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = "opencode",
                HarnessResumeToken = (string?)null,
                IsHidden = false,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                GitBaselineRef = gitBaselineRef,
                GitRepoRoot = gitRepoRoot,
                UserId = "local-user"
            });
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed. stdout: {standardOutput} stderr: {standardError}");
    }
}
