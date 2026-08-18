using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionFileBrowserEndpointTests : IAsyncDisposable
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly TempDirectory _tempDirectory;

    public SessionFileBrowserEndpointTests()
    {
        _tempDirectory = new TempDirectory();
        
        _factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                // Replace SessionOrchestrator with a configured one
                services.AddSingleton<SessionOrchestrator>(sp =>
                {
                    var builder = new SessionOrchestratorBuilder()
                        .WithUserContext(new TestUserContext("test-user"));

                    builder.WorkspaceRootRepository.Seed(
                        new WorkspaceRoot
                        {
                            Id = "root-1",
                            Path = Path.GetTempPath(),
                            CreatedAt = DateTime.UtcNow.ToString("O")
                        }
                    );

                    builder.ProjectRepository.Seed(new Project
                    {
                        Id = "scratch-1",
                        Name = "Scratch",
                        Type = "scratch",
                        Position = 0,
                        CreatedAt = "2026-01-01",
                        UpdatedAt = "2026-01-01"
                    });

                    var runtime = builder.RegisterHarness("opencode", "OpenCode");
                    runtime.DefaultSession = new FakeHarnessSession("inst-1");

                    return builder.Build();
                });
            });

        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _tempDirectory.Dispose();
    }

    [Fact]
    public async Task browse_and_content_endpoints_work_consistently()
    {
        // Arrange: create a session with a file in a subdirectory
        var subDir = Path.Combine(_tempDirectory.Path, ".weave");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "weave.log");
        await File.WriteAllTextAsync(filePath, "test content from file");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "File Browser Test"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        createResult.ShouldNotBeNull();
        var sessionId = createResult.Session.Id;

        // Act: browse the subdirectory
        var browseResponse = await _client.GetAsync($"/api/sessions/{sessionId}/files/browse?path=.weave");
        browseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var browseResult = await browseResponse.Content.ReadFromJsonAsync<BrowseSessionDirectoryResponse>();
        browseResult.ShouldNotBeNull();
        browseResult.Entries.ShouldContain(e => e.Name == "weave.log");

        // Act: read the file content using forward slash
        var contentResponse = await _client.GetAsync($"/api/sessions/{sessionId}/files/content?path=.weave/weave.log");
        
        // Assert: should return 200, not 404
        if (contentResponse.StatusCode != HttpStatusCode.OK)
        {
            var errorContent = await contentResponse.Content.ReadAsStringAsync();
            Assert.Fail($"Expected 200 OK but got {contentResponse.StatusCode}. Response: {errorContent}");
        }

        contentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentResult = await contentResponse.Content.ReadFromJsonAsync<ReadSessionFileResponse>();
        contentResult.ShouldNotBeNull();
        contentResult.Content.ShouldBe("test content from file");
    }

    [Fact]
    public async Task content_endpoint_returns_400_when_path_is_missing()
    {
        // Arrange: create a session
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "File Browser Test"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        createResult.ShouldNotBeNull();
        var sessionId = createResult.Session.Id;

        // Act: try to read without path parameter
        var contentResponse = await _client.GetAsync($"/api/sessions/{sessionId}/files/content");

        // Assert: should return 400 Bad Request
        contentResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task content_endpoint_works_with_url_encoded_path()
    {
        // Arrange: create a session with a file in a subdirectory
        var subDir = Path.Combine(_tempDirectory.Path, ".weave");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "weave.log");
        await File.WriteAllTextAsync(filePath, "test content from file");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "File Browser Test"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        createResult.ShouldNotBeNull();
        var sessionId = createResult.Session.Id;

        // Act: read the file content using URL-encoded path (forward slash encoded as %2F)
        var encodedPath = Uri.EscapeDataString(".weave/weave.log");
        var contentResponse = await _client.GetAsync($"/api/sessions/{sessionId}/files/content?path={encodedPath}");

        // Assert: should return 200
        contentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentResult = await contentResponse.Content.ReadFromJsonAsync<ReadSessionFileResponse>();
        contentResult.ShouldNotBeNull();
        contentResult.Content.ShouldBe("test content from file");
    }

    [Fact]
    public async Task content_endpoint_handles_backslash_paths()
    {
        // Arrange: create a session with a file in a subdirectory
        var subDir = Path.Combine(_tempDirectory.Path, ".weave");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "weave.log");
        await File.WriteAllTextAsync(filePath, "test content from file");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "File Browser Test"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>();
        createResult.ShouldNotBeNull();
        var sessionId = createResult.Session.Id;

        // Act: read the file content using backslash (Windows-style path)
        var contentResponse = await _client.GetAsync($"/api/sessions/{sessionId}/files/content?path=.weave\\weave.log");

        // Assert: should return 200 (Path.Combine should handle both separators)
        contentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentResult = await contentResponse.Content.ReadFromJsonAsync<ReadSessionFileResponse>();
        contentResult.ShouldNotBeNull();
        contentResult.Content.ShouldBe("test content from file");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-file-browser-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
