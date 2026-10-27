using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The bar under a waiting page sends or skips the user's pick. Only the user: an agent reaching Fleet through its
/// own address can't answer the page it showed.
/// </summary>
public sealed class PageChoiceEndpointTests : IClassFixture<PageChoiceEndpointTests.Server>, IDisposable
{
    private const string Owner = "local-user";
    private const string Token = "token-1";
    private const string Session = "sess-layouts";

    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PageChoiceEndpointTests(Server server)
    {
        _factory = server.Factory;
        _client = _factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private PageChoices Choices => _factory.Services.GetRequiredService<PageChoices>();

    [Fact]
    public async Task The_user_sends_the_pick_once()
    {
        Choices.Start(Session, "call-1", "Layouts");

        var sent = await _client.PostAsJsonAsync($"/api/sessions/{Session}/page-choices/call-1/send", new { value = "b", summary = "Layout B" });
        var again = await _client.PostAsJsonAsync($"/api/sessions/{Session}/page-choices/call-1/send", new { value = "a", summary = "Layout A" });

        sent.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Choices.NextAsync(Session, "call-1", TimeSpan.Zero, CancellationToken.None)).ShouldBe(PageChoiceAnswer.Picked("b", "Layout B"));
    }

    [Fact]
    public async Task An_agent_cannot_answer_its_own_page()
    {
        Choices.Start(Session, "call-2", "Layouts");

        var sent = await _client.PostAsJsonAsync($"/agent/{Token}/api/sessions/{Session}/page-choices/call-2/send", new { value = "b" });
        var skipped = await _client.PostAsync($"/agent/{Token}/api/sessions/{Session}/page-choices/call-2/skip", null);

        sent.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        skipped.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Choices.NextAsync(Session, "call-2", TimeSpan.Zero, CancellationToken.None)).ShouldBeNull();
    }

    public sealed class Server : IAsyncLifetime
    {
        public ApiWebApplicationFactory Factory { get; } = new(
            authEnabled: false,
            simulateLocalhostRequest: true,
            configureTestServices: services => services.AddSingleton<IHarnessBridgeTokens>(new FakeTokens()));

        public async Task InitializeAsync()
        {
            using var scope = Factory.Services.CreateScope();
            using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await connection.ExecuteAsync($"""
                INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
                VALUES ('ws-1', '/ws', 'Work', '2026-10-01T00:00:00+00:00', '{Owner}');
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-01T00:00:00+00:00', '{Owner}');
                INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title, status, directory, harness_type,
                    lifecycle_status, retention_status, created_at, user_id)
                VALUES ('{Session}', 'ws-1', 'inst-1', 'ses_layouts', 'Settings screen', 'active', '/ws', 'opencode2',
                        'running', 'active', '2026-10-01T10:00:00+00:00', '{Owner}');
                """);
        }

        public async Task DisposeAsync() => await Factory.DisposeAsync();
    }

    private sealed class FakeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == Token;
    }
}
