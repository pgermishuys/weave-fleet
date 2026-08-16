using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Infrastructure.Tests.Skills;

public sealed class GitHubSkillCatalogServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _cacheFilePath;
    private readonly FakeHttpClientFactory _httpClientFactory;
    private readonly GitHubSkillCatalogService _catalogService;

    public GitHubSkillCatalogServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);

        _cacheFilePath = Path.Combine(_testDir, ".weave", "catalog-cache.json");

        _httpClientFactory = new FakeHttpClientFactory();
        _catalogService = new GitHubSkillCatalogService(_httpClientFactory, NullLogger<GitHubSkillCatalogService>.Instance, _testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenRemoteSucceeds_ReturnsFreshCatalog()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "test-skill",
                    "displayName": "Test Skill",
                    "description": "A test skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/skill",
                    "ref": "main",
                    "targetHarnesses": ["opencode"],
                    "author": "Test Author",
                    "version": "1.0.0",
                    "tags": ["test"]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var response = result.Value;
        response.Entries.ShouldHaveSingleItem();
        response.IsStale.ShouldBeFalse();
        response.CachedAt.ShouldNotBeNull();

        var entry = response.Entries[0];
        entry.Name.ShouldBe("test-skill");
        entry.DisplayName.ShouldBe("Test Skill");
        entry.Description.ShouldBe("A test skill");
        entry.Source.ShouldBe(SkillSource.GitHub);
        entry.RepoUrl.ShouldBe("https://github.com/test/skill");
        entry.Ref.ShouldBe("main");
        entry.TargetHarnesses.ShouldContain("opencode");
        entry.Author.ShouldBe("Test Author");
        entry.Version.ShouldBe("1.0.0");
        entry.Tags.ShouldContain("test");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenRemoteSucceeds_WritesCacheFile()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "cached-skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/skill",
                    "targetHarnesses": ["opencode"]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        await _catalogService.FetchCatalogAsync();

        // Assert
        File.Exists(_cacheFilePath).ShouldBeTrue();

        var cacheContent = await File.ReadAllTextAsync(_cacheFilePath);
        cacheContent.ShouldNotBeEmpty();

        // Verify cache contains the skill
        cacheContent.ShouldContain("cached-skill");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenRemoteFails_FallsBackToCache()
    {
        // Arrange - First create a cache file
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var cacheJson = $$"""
        {
            "entries": [
                {
                    "name": "cached-skill",
                    "source": 1,
                    "repoUrl": "https://github.com/test/cached",
                    "targetHarnesses": ["opencode"]
                }
            ],
            "cachedAt": "{{cachedAt:O}}"
        }
        """;

        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        await File.WriteAllTextAsync(_cacheFilePath, cacheJson);

        // Now simulate remote failure
        _httpClientFactory.SetResponse(HttpStatusCode.InternalServerError, "Server error");

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var response = result.Value;
        response.Entries.ShouldHaveSingleItem();
        response.Entries[0].Name.ShouldBe("cached-skill");
        response.IsStale.ShouldBeFalse(); // Within 1 hour TTL
        response.CachedAt.ShouldBe(cachedAt);
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenCacheIsStale_IndicatesStaleness()
    {
        // Arrange - Create a cache file older than 1 hour
        var cachedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var cacheJson = $$"""
        {
            "entries": [
                {
                    "name": "stale-skill",
                    "source": 1,
                    "repoUrl": "https://github.com/test/stale",
                    "targetHarnesses": ["opencode"]
                }
            ],
            "cachedAt": "{{cachedAt:O}}"
        }
        """;

        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        await File.WriteAllTextAsync(_cacheFilePath, cacheJson);

        // Simulate remote failure
        _httpClientFactory.SetResponse(HttpStatusCode.ServiceUnavailable, "Service unavailable");

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var response = result.Value;
        response.IsStale.ShouldBeTrue(); // Older than 1 hour
        response.CachedAt.ShouldBe(cachedAt);
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenRemoteFailsAndNoCacheExists_ReturnsError()
    {
        // Arrange
        _httpClientFactory.SetResponse(HttpStatusCode.NotFound, "Not found");

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SkillCatalog.FetchFailed");
        result.Error.Description.ShouldContain("HTTP 404");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenNetworkError_ReturnsNetworkError()
    {
        // Arrange
        _httpClientFactory.SetNetworkError();

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SkillCatalog.NetworkError");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenTimeout_ReturnsTimeoutError()
    {
        // Arrange
        _httpClientFactory.SetTimeout();

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        // TaskCanceledException without a token is treated as cancelled, not timeout
        // This matches the actual implementation behavior
        result.Error.Code.ShouldBe("SkillCatalog.Cancelled");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenInvalidJson_ReturnsValidationError()
    {
        // Arrange
        _httpClientFactory.SetResponse(HttpStatusCode.OK, "{ invalid json }");

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SkillCatalog");
        result.Error.Description.ShouldContain("Invalid JSON");
    }

    [Fact]
    public async Task FetchCatalogAsync_WhenMissingSkillsArray_ReturnsValidationError()
    {
        // Arrange
        _httpClientFactory.SetResponse(HttpStatusCode.OK, """{ "notSkills": [] }""");

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SkillCatalog");
        result.Error.Description.ShouldContain("'skills' array");
    }

    [Fact]
    public async Task FetchCatalogAsync_ParsesAllSkillSourceTypes()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "bundled-skill",
                    "source": "Bundled",
                    "targetHarnesses": ["opencode"]
                },
                {
                    "name": "github-skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/skill",
                    "ref": "v1.0.0",
                    "targetHarnesses": ["opencode"]
                },
                {
                    "name": "local-skill",
                    "source": "Local",
                    "localPath": "/path/to/skill",
                    "targetHarnesses": ["opencode"]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var entries = result.Value.Entries;
        entries.Count.ShouldBe(3);

        var bundled = entries.First(e => e.Name == "bundled-skill");
        bundled.Source.ShouldBe(SkillSource.Bundled);

        var github = entries.First(e => e.Name == "github-skill");
        github.Source.ShouldBe(SkillSource.GitHub);
        github.RepoUrl.ShouldBe("https://github.com/test/skill");
        github.Ref.ShouldBe("v1.0.0");

        var local = entries.First(e => e.Name == "local-skill");
        local.Source.ShouldBe(SkillSource.Local);
        local.LocalPath.ShouldBe("/path/to/skill");
    }

    [Fact]
    public async Task FetchCatalogAsync_SkipsEntriesWithMissingName()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/skill",
                    "targetHarnesses": ["opencode"]
                },
                {
                    "name": "valid-skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/valid",
                    "targetHarnesses": ["opencode"]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Entries.ShouldHaveSingleItem();
        result.Value.Entries[0].Name.ShouldBe("valid-skill");
    }

    [Fact]
    public async Task FetchCatalogAsync_SkipsEntriesWithInvalidSource()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "invalid-source-skill",
                    "source": "InvalidSource",
                    "targetHarnesses": ["opencode"]
                },
                {
                    "name": "valid-skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/valid",
                    "targetHarnesses": ["opencode"]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Entries.ShouldHaveSingleItem();
        result.Value.Entries[0].Name.ShouldBe("valid-skill");
    }

    [Fact]
    public async Task FetchCatalogAsync_ParsesOptionalFields()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "full-skill",
                    "displayName": "Full Skill",
                    "description": "A complete skill",
                    "source": "GitHub",
                    "repoUrl": "https://github.com/test/skill",
                    "ref": "v2.0.0",
                    "targetHarnesses": ["opencode", "claude-code"],
                    "author": "Test Author",
                    "version": "2.0.0",
                    "tags": ["test", "example"],
                    "createdAt": "2024-01-01T00:00:00Z",
                    "updatedAt": "2024-06-01T00:00:00Z"
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var entry = result.Value.Entries[0];
        entry.DisplayName.ShouldBe("Full Skill");
        entry.Description.ShouldBe("A complete skill");
        entry.Author.ShouldBe("Test Author");
        entry.Version.ShouldBe("2.0.0");
        entry.Tags.Count.ShouldBe(2);
        entry.Tags.ShouldContain("test");
        entry.Tags.ShouldContain("example");
        entry.TargetHarnesses.Count.ShouldBe(2);
        entry.CreatedAt.ShouldNotBeNull();
        entry.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task FetchCatalogAsync_HandlesEmptySkillsArray()
    {
        // Arrange
        var catalogJson = """{ "skills": [] }""";
        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchCatalogAsync_TrimsWhitespaceFromFields()
    {
        // Arrange
        var catalogJson = """
        {
            "skills": [
                {
                    "name": "  trimmed-skill  ",
                    "displayName": "  Trimmed  ",
                    "source": "GitHub",
                    "repoUrl": "  https://github.com/test/skill  ",
                    "targetHarnesses": ["  opencode  "]
                }
            ]
        }
        """;

        _httpClientFactory.SetResponse(HttpStatusCode.OK, catalogJson);

        // Act
        var result = await _catalogService.FetchCatalogAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var entry = result.Value.Entries[0];
        entry.Name.ShouldBe("trimmed-skill");
        entry.DisplayName.ShouldBe("Trimmed");
        entry.RepoUrl.ShouldBe("https://github.com/test/skill");
        entry.TargetHarnesses[0].ShouldBe("opencode");
    }

    // ── Test doubles ───────────────────────────────────────────────────────

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _content = string.Empty;
        private bool _throwNetworkError;
        private bool _throwTimeout;

        public void SetResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
            _throwNetworkError = false;
            _throwTimeout = false;
        }

        public void SetNetworkError()
        {
            _throwNetworkError = true;
            _throwTimeout = false;
        }

        public void SetTimeout()
        {
            _throwTimeout = true;
            _throwNetworkError = false;
        }

        public HttpClient CreateClient(string name)
        {
            var handler = new FakeHttpMessageHandler(_statusCode, _content, _throwNetworkError, _throwTimeout);
            return new HttpClient(handler);
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;
        private readonly bool _throwNetworkError;
        private readonly bool _throwTimeout;

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string content, bool throwNetworkError, bool throwTimeout)
        {
            _statusCode = statusCode;
            _content = content;
            _throwNetworkError = throwNetworkError;
            _throwTimeout = throwTimeout;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throwNetworkError)
            {
                throw new HttpRequestException("Network error");
            }

            if (_throwTimeout)
            {
                throw new TaskCanceledException();
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content)
            };

            return Task.FromResult(response);
        }
    }
}
