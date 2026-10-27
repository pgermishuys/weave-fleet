using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionFileBrowserEndpointTests : IClassFixture<SessionFileBrowserEndpointTests.Server>, IDisposable
{
    private readonly HttpClient _client;
    private readonly TempDirectory _tempDirectory;

    public SessionFileBrowserEndpointTests(Server server)
    {
        _tempDirectory = new TempDirectory();
        _client = server.Factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
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
    public async Task attachments_endpoint_writes_the_body_and_answers_in_camel_case()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new { directory = _tempDirectory.Path, title = "Attach" });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        var content = new ByteArrayContent("one\ntwo\n"u8.ToArray());
        content.Headers.ContentType = new("text/plain");
        var response = await _client.PostAsync($"/api/sessions/{sessionId}/attachments?name=build%20output.log", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("path").GetString()!.ShouldStartWith(".weave/attachments/");
        root.GetProperty("name").GetString().ShouldBe("build-output.log");
        root.GetProperty("size").GetInt64().ShouldBe(8);
        root.GetProperty("lines").GetInt32().ShouldBe(2);
        root.GetProperty("mime").GetString().ShouldBe("text/plain");
        File.Exists(Path.Combine(_tempDirectory.Path, root.GetProperty("path").GetString()!)).ShouldBeTrue();

        var noName = await _client.PostAsync($"/api/sessions/{sessionId}/attachments", new ByteArrayContent([1]));
        noName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task clearing_attachments_answers_204_deletes_the_files_and_is_not_found_for_an_unknown_session()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new { directory = _tempDirectory.Path, title = "Attach" });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;
        await _client.PostAsync($"/api/sessions/{sessionId}/attachments?name=a.log", new ByteArrayContent([1]));

        var cleared = await _client.DeleteAsync($"/api/sessions/{sessionId}/attachments");

        cleared.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(Path.Combine(_tempDirectory.Path, ".weave")).ShouldBeFalse();
        (await _client.DeleteAsync("/api/sessions/nope/attachments")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    [Fact]
    public async Task find_files_lists_folders_for_empty_and_folder_queries_and_searches_otherwise()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory.Path, "src", "components"));
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.Path, "src", "components", "Button.vue"), "");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.Path, "README.md"), "");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "Find Files Test"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        async Task<string[]> Find(string query)
        {
            var response = await _client.GetAsync($"/api/sessions/{sessionId}/find/files?q={Uri.EscapeDataString(query)}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("files").EnumerateArray().Select(file => file.GetString()!).ToArray();
        }

        (await Find("")).ShouldBe(["src/", "README.md"]);
        (await Find("src/")).ShouldBe(["src/components/"]);
        (await Find("comp")).ShouldBe(["src/components/", "src/components/Button.vue"]);
    }

    [Fact]
    public async Task find_text_returns_matches_grouped_by_file_and_a_bad_regex_is_a_400_with_the_reason()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.Path, "src", "Retry.cs"), "const int MaxAttempts = 5;\nint other;\n");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.Path, "README.md"), "Up to MaxAttempts times.\n");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new { directory = _tempDirectory.Path, title = "Find Text Test" });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        var found = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/find/text?q=maxattempts");
        found.GetProperty("matches").GetInt32().ShouldBe(2);
        found.GetProperty("engine").GetString().ShouldBe("walk");
        found.GetProperty("stopped").ValueKind.ShouldBe(JsonValueKind.Null);
        var first = found.GetProperty("files")[0];
        first.GetProperty("path").GetString().ShouldBe("README.md");
        first.GetProperty("total").GetInt32().ShouldBe(1);
        var match = first.GetProperty("matches")[0];
        match.GetProperty("line").GetInt32().ShouldBe(1);
        match.GetProperty("ranges")[0].GetProperty("start").GetInt32().ShouldBe(6);

        var strict = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/find/text?q=maxattempts&matchCase=true");
        strict.GetProperty("matches").GetInt32().ShouldBe(0);

        var bad = await _client.GetAsync($"/api/sessions/{sessionId}/find/text?q={Uri.EscapeDataString("a[")}&regex=true");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).ShouldContain("regex");

        (await _client.GetAsync("/api/sessions/nope/find/text?q=x")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task saving_a_file_returns_the_new_hash_and_a_stale_save_returns_409_with_the_current_file()
    {
        var path = Path.Combine(_tempDirectory.Path, "app.ts");
        await File.WriteAllTextAsync(path, "const one = 1;\n");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "Save Test"
        });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        var read = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/files/content?path=app.ts");
        var readHash = read.GetProperty("hash").GetString();
        readHash.ShouldNotBeNullOrEmpty();

        var save = await _client.PutAsJsonAsync($"/api/sessions/{sessionId}/files/content", new
        {
            path = "app.ts",
            content = "const one = 111;\n",
            baseHash = readHash,
        });
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        var newHash = saved.GetProperty("hash").GetString();
        newHash.ShouldNotBe(readHash);
        (await File.ReadAllTextAsync(path)).ShouldBe("const one = 111;\n");

        // Saving again from the first read loses: the file changed since.
        var stale = await _client.PutAsJsonAsync($"/api/sessions/{sessionId}/files/content", new
        {
            path = "app.ts",
            content = "const one = 2;\n",
            baseHash = readHash,
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        conflict.GetProperty("content").GetString().ShouldBe("const one = 111;\n");
        conflict.GetProperty("hash").GetString().ShouldBe(newHash);
        (await File.ReadAllTextAsync(path)).ShouldBe("const one = 111;\n");
    }

    [Fact]
    public async Task saving_outside_the_session_is_400_and_a_missing_file_is_404()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "Save Errors Test"
        });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        var traversal = await _client.PutAsJsonAsync($"/api/sessions/{sessionId}/files/content", new
        {
            path = "../outside.txt",
            content = "x",
            baseHash = "abc",
        });
        traversal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var missing = await _client.PutAsJsonAsync($"/api/sessions/{sessionId}/files/content", new
        {
            path = "missing.txt",
            content = "x",
            baseHash = "abc",
        });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task image_endpoint_serves_an_image_of_any_size_sandboxed_and_refuses_other_files()
    {
        var bytes = new byte[600 * 1024];
        bytes[0] = 0x89;
        Directory.CreateDirectory(Path.Combine(_tempDirectory.Path, "docs"));
        await File.WriteAllBytesAsync(Path.Combine(_tempDirectory.Path, "docs", "map.png"), bytes);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.Path, "notes.txt"), "notes");

        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            directory = _tempDirectory.Path,
            title = "Image Test"
        });
        var sessionId = (await createResponse.Content.ReadFromJsonAsync<CreateSessionApiResponse>())!.Session.Id;

        var image = await _client.GetAsync($"/api/sessions/{sessionId}/files/image?path=docs/map.png");
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        image.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"]);
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);

        (await _client.GetAsync($"/api/sessions/{sessionId}/files/image?path=notes.txt")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"/api/sessions/{sessionId}/files/image?path=../outside.png")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"/api/sessions/{sessionId}/files/image?path=gone.png")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(
            authEnabled: false,
            configureTestServices: services =>
            {
                // The endpoints these tests call use the session services; replace them with ones over in-memory fakes.
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

                var orchestrator = builder.Build();
                services.AddSingleton(orchestrator);
                services.AddSingleton(builder.Creation);
                services.AddSingleton(builder.Files);
                services.AddSingleton(new WeaveFleet.Application.Sessions.Files.SessionAttachments(
                    builder.SessionRepository, Microsoft.Extensions.Logging.Abstractions.NullLogger<WeaveFleet.Application.Sessions.Files.SessionAttachments>.Instance));
            });

        public void Dispose() => Factory.Dispose();
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
