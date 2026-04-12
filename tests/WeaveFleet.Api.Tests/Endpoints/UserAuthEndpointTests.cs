using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UserAuthEndpointTests
{
    [Fact]
    public async Task GetUserMe_WhenAuthenticated_ReturnsOk()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UserMePayload>();
        payload.ShouldNotBeNull();
        payload.UserId.ShouldBe("test-user");
        payload.Email.ShouldBe("test@example.com");
        payload.DisplayName.ShouldBe("Test User");
        payload.OnboardingCompleted.ShouldBeFalse();
        payload.OnboardingStatus.Completed.ShouldBeFalse();
        payload.OnboardingStatus.HasStoredCredentials.ShouldBeFalse();
        payload.OnboardingStatus.HasCreatedSession.ShouldBeFalse();
    }

    [Fact]
    public async Task GetUserMe_WhenUnauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebSocketHandshake_WhenUnauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        using var request = CreateWebSocketHandshakeRequest("http://localhost:3001");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task WebSocketHandshake_WhenAuthenticatedWithDisallowedOrigin_ReturnsForbidden()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        using var request = CreateWebSocketHandshakeRequest("https://evil.example");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateConfig_WithoutCsrfToken_IsRejected()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PutAsync(
            "/api/config",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateConfig_WithCsrfToken_Succeeds()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var meResponse = await client.GetAsync("/api/user/me");
        meResponse.EnsureSuccessStatusCode();

        var csrfToken = meResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/config")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_WithoutCsrfToken_IsRejected()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PostAsync("/auth/logout", new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetClientConfig_WhenAuthDisabled_ReturnsClientFlags()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/config/client");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ClientConfigPayload>();
        payload.ShouldNotBeNull();
        payload.AuthEnabled.ShouldBeFalse();
        payload.AvailableHarnesses.ShouldContain("opencode");
        payload.AvailableHarnesses.ShouldContain("claude-code");
    }

    [Fact]
    public async Task GetUserMe_WhenAuthDisabled_ReturnsLocalUser()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UserMePayload>();
        payload.ShouldNotBeNull();
        payload.UserId.ShouldBe("local-user");
        payload.OnboardingStatus.Completed.ShouldBeFalse();
        payload.OnboardingStatus.HasStoredCredentials.ShouldBeFalse();
        payload.OnboardingStatus.HasCreatedSession.ShouldBeFalse();
    }

    [Fact]
    public async Task GetUserMe_WhenUserHasCredentialsAndSessions_ReturnsOnboardingStatus()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        await SeedAuthenticatedUserOnboardingDataAsync(factory.Services);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UserMePayload>();
        payload.ShouldNotBeNull();
        payload.OnboardingCompleted.ShouldBeTrue();
        payload.OnboardingStatus.Completed.ShouldBeTrue();
        payload.OnboardingStatus.HasStoredCredentials.ShouldBeTrue();
        payload.OnboardingStatus.HasCreatedSession.ShouldBeTrue();
    }

    [Fact]
    public async Task Login_WithExternalReturnUrl_FallsBackToRoot()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/auth/login?returnUrl=//evil.example/path");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location.OriginalString.ShouldBe("/");
    }

    private static string? ExtractCookieValue(IEnumerable<string> setCookies, string cookieName)
    {
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';');
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header.Substring(cookieName.Length + 1);
        }

        return null;
    }

    private static HttpRequestMessage CreateWebSocketHandshakeRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ws");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static async Task SeedAuthenticatedUserOnboardingDataAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        const string userId = "test-user";
        const string createdAt = "2026-01-01T00:00:00.0000000Z";
        const string completedAt = "2026-01-02T00:00:00.0000000Z";

        await connection.ExecuteAsync(
            "INSERT INTO users (id, email, display_name, status, created_at, last_login_at, onboarding_completed_at) VALUES (@Id, @Email, @DisplayName, @Status, @CreatedAt, @LastLoginAt, @OnboardingCompletedAt)",
            new
            {
                Id = userId,
                Email = "test@example.com",
                DisplayName = "Test User",
                Status = "active",
                CreatedAt = createdAt,
                LastLoginAt = createdAt,
                OnboardingCompletedAt = completedAt
            });

        await connection.ExecuteAsync(
            "UPDATE users SET onboarding_completed_at = @CompletedAt WHERE id = @Id",
            new { Id = userId, CompletedAt = completedAt });

        await connection.ExecuteAsync(
            "INSERT INTO user_credentials (id, user_id, namespace, kind, label, encrypted_value, display_hint, metadata, created_at, updated_at) VALUES (@Id, @UserId, @Namespace, @Kind, @Label, @EncryptedValue, @DisplayHint, @Metadata, @CreatedAt, @UpdatedAt)",
            new
            {
                Id = "cred-1",
                UserId = userId,
                Namespace = "anthropic",
                Kind = "api-key",
                Label = "Work Key",
                EncryptedValue = "ciphertext",
                DisplayHint = "1234",
                Metadata = (string?)null,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = "workspace-1",
                Directory = "/tmp/workspace-1",
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = (string?)null,
                UserId = userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = "instance-1",
                Port = 0,
                Pid = (int?)null,
                Directory = "/tmp/workspace-1",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = "session-1",
                WorkspaceId = "workspace-1",
                InstanceId = "instance-1",
                ProjectId = (string?)null,
                OpencodeSessionId = "opencode-1",
                Title = "Started Session",
                Status = "active",
                Directory = "/tmp/workspace-1",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = (string?)null,
                ActivityStatus = (string?)null,
                LifecycleStatus = (string?)null,
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = "opencode",
                HarnessResumeToken = (string?)null,
                IsHidden = false,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                UserId = userId
            });
    }

    private sealed record UserMePayload(string UserId, string? Email, string? DisplayName, bool OnboardingCompleted, OnboardingStatusPayload OnboardingStatus, string CreatedAt);
    private sealed record OnboardingStatusPayload(bool Completed, bool HasStoredCredentials, bool HasCreatedSession);
    private sealed record ClientConfigPayload(bool CloudMode, bool AuthEnabled, IReadOnlyList<string> AvailableHarnesses);
}
