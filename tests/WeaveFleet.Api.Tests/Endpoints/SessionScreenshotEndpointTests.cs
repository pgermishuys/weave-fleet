using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The screenshots agents took, as the conversation fetches them for their tool rows. Shots are saved straight
/// into the store for the authenticated test user's session (sub=test-user) and for other-user's.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionScreenshotEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;
    private string _mine = "";
    private string _theirs = "";

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-mine', '/ws-mine', 'Mine', '2026-09-01T00:00:00+00:00', 'test-user'),
                   ('ws-other', '/ws-other', 'Other', '2026-09-01T00:00:00+00:00', 'other-user');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-mine', 0, NULL, '/ws-mine', '', 'stopped', '2026-09-01T00:00:00+00:00', 'test-user'),
                   ('inst-other', 0, NULL, '/ws-other', '', 'stopped', '2026-09-01T00:00:00+00:00', 'other-user');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('sess-mine', 'ws-mine', 'inst-mine', 'oc-mine', 'Mine', 'stopped', '/ws-mine',
                    'stopped', 'active', '2026-09-01T10:00:00+00:00', 'test-user'),
                   ('sess-other', 'ws-other', 'inst-other', 'oc-other', 'Other', 'stopped', '/ws-other',
                    'stopped', 'active', '2026-09-01T12:00:00+00:00', 'other-user');
            """);

        var store = scope.ServiceProvider.GetRequiredService<ISessionScreenshotStore>();
        _mine = (await store.SaveAsync("sess-mine", Png))!;
        _theirs = (await store.SaveAsync("sess-other", Png))!;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_screenshot_in_your_session_comes_back_as_a_png_the_browser_can_keep()
    {
        var response = await _client!.GetAsync($"/api/sessions/sess-mine/screenshots/{_mine}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
        response.Headers.CacheControl!.Private.ShouldBeTrue();
    }

    [Fact]
    public async Task Someone_else_s_screenshot_is_not_found_even_by_its_id()
    {
        (await _client!.GetAsync($"/api/sessions/sess-other/screenshots/{_theirs}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client!.GetAsync($"/api/sessions/sess-mine/screenshots/{_theirs}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("sess-mine", "shot_missing")]
    [InlineData("sess-missing", "shot_missing")]
    [InlineData("sess-mine", "..%2F..%2Ffleet")]
    public async Task A_screenshot_that_is_not_there_is_not_found(string sessionId, string screenshotId)
    {
        (await _client!.GetAsync($"/api/sessions/{sessionId}/screenshots/{screenshotId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
