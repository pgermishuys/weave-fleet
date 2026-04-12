using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Shouldly;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.SessionSources;

namespace WeaveFleet.Infrastructure.Tests.SessionSources;

public sealed class GitHubSessionSourceProviderTests
{
    private const string TestUserId = "test-user";

    [Fact]
    public async Task ResolveAsync_TruncatesOversizedContextAndLabelsOrigin()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = SessionSourceTypeNames.GitHubIssue,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            Input = JsonSerializer.SerializeToElement(new
            {
                owner = "acme",
                repo = "rocket",
                number = 42
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.ContextEnvelope.ShouldNotBeNull();
        result.Value.Input.ContextEnvelope.OriginLabel.ShouldBe("GitHub issue #42");
        result.Value.Input.ContextEnvelope.IsTruncated.ShouldBeTrue();
        result.Value.Input.ContextEnvelope.Content.Length.ShouldBe(12000);
        result.Value.Input.Provenance.ResourceId.ShouldBe("acme/rocket#42");
        result.Value.Input.Provenance.ResourceUrl.ShouldBe("https://github.com/acme/rocket/issues/42");
    }

    [Fact]
    public async Task ResolveAsync_RedactsSecretLikeGitHubBodyAndComments()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = SessionSourceTypeNames.GitHubIssue,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            Input = JsonSerializer.SerializeToElement(new
            {
                owner = "acme",
                repo = "rocket",
                number = 99
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.ContextEnvelope.ShouldNotBeNull();
        result.Value.Input.ContextEnvelope.Content.ShouldContain("[redacted: potential secret]");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("ghp_secretvalue123");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("Authorization: Bearer super-secret");
    }

    [Fact]
    public async Task ResolveAsync_RedactsSecretLikeGitHubTitleAndPrivateKeyBlocks()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = SessionSourceTypeNames.GitHubIssue,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            Input = JsonSerializer.SerializeToElement(new
            {
                owner = "acme",
                repo = "rocket",
                number = 100
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.ContextEnvelope.ShouldNotBeNull();
        result.Value.Input.ContextEnvelope.Content.ShouldContain("[redacted: potential secret]");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("ghp_title_secret_456");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----BEGIN PRIVATE KEY-----");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("abc123privatekeymaterial");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----END PRIVATE KEY-----");
    }

    [Fact]
    public async Task ResolveAsync_RedactsAdditionalPrivateKeyPemVariants()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.ResolveAsync(new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = SessionSourceTypeNames.GitHubIssue,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            Input = JsonSerializer.SerializeToElement(new
            {
                owner = "acme",
                repo = "rocket",
                number = 101
            })
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Input.ContextEnvelope.ShouldNotBeNull();
        result.Value.Input.ContextEnvelope.Content.ShouldContain("[redacted: potential secret]");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----BEGIN ENCRYPTED PRIVATE KEY-----");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----END ENCRYPTED PRIVATE KEY-----");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----BEGIN DSA PRIVATE KEY-----");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("-----END DSA PRIVATE KEY-----");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("encryptedkeymaterial");
        result.Value.Input.ContextEnvelope.Content.ShouldNotContain("dsakeymaterial");
    }

    private static (GitHubSessionSourceProvider Provider, IPluginStateStore Store) CreateProvider()
    {
        var pluginStateStore = Substitute.For<IPluginStateStore>();
        pluginStateStore.GetStateAsync("github", TestUserId, Arg.Any<CancellationToken>()).Returns(new JsonObject
        {
            ["access_token"] = "token"
        });

        var httpClientFactory = new TestHttpClientFactory(new FakeGitHubHandler());
        var credentialRepository = Substitute.For<IUserCredentialRepository>();
        var credentialProtector = Substitute.For<ICredentialProtector>();
        credentialProtector.Decrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        credentialRepository.ListByUserNamespaceAndKindAsync(TestUserId, "github", "oauth-access-token").Returns([
            new UserCredential
            {
                Id = "cred-1",
                UserId = TestUserId,
                Namespace = "github",
                Kind = "oauth-access-token",
                Label = "GitHub",
                EncryptedValue = "token",
                DisplayHint = "...oken",
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z"
            }
        ]);

        var gitHubService = new GitHubService(httpClientFactory, pluginStateStore, credentialRepository, credentialProtector);
        var gitHubApiProxy = new GitHubApiProxy(httpClientFactory);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(TestUserId);

        var provider = new GitHubSessionSourceProvider(gitHubService, gitHubApiProxy, userContext);
        return (provider, pluginStateStore);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            JsonNode body = path switch
            {
                "/repos/acme/rocket/issues/42" => new JsonObject
                {
                    ["title"] = "Release build fails",
                    ["body"] = new string('A', 13050),
                    ["html_url"] = "https://github.com/acme/rocket/issues/42"
                },
                "/repos/acme/rocket/issues/42/comments" => new JsonArray
                {
                    new JsonObject
                    {
                        ["body"] = "Needs a release-mode repro.",
                        ["user"] = new JsonObject { ["login"] = "reviewer" }
                    }
                },
                "/repos/acme/rocket/issues/99" => new JsonObject
                {
                    ["title"] = "Contains secrets",
                    ["body"] = "Keep this summary\napi_key=ghp_secretvalue123\nStill useful context",
                    ["html_url"] = "https://github.com/acme/rocket/issues/99"
                },
                "/repos/acme/rocket/issues/99/comments" => new JsonArray
                {
                    new JsonObject
                    {
                        ["body"] = "Authorization: Bearer super-secret",
                        ["user"] = new JsonObject { ["login"] = "reviewer" }
                    }
                },
                "/repos/acme/rocket/issues/100" => new JsonObject
                {
                    ["title"] = "token ghp_title_secret_456",
                    ["body"] = "-----BEGIN PRIVATE KEY-----\nabc123privatekeymaterial\n-----END PRIVATE KEY-----",
                    ["html_url"] = "https://github.com/acme/rocket/issues/100"
                },
                "/repos/acme/rocket/issues/100/comments" => new JsonArray(),
                "/repos/acme/rocket/issues/101" => new JsonObject
                {
                    ["title"] = "PEM variants",
                    ["body"] = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nencryptedkeymaterial\n-----END ENCRYPTED PRIVATE KEY-----\n-----BEGIN DSA PRIVATE KEY-----\ndsakeymaterial\n-----END DSA PRIVATE KEY-----",
                    ["html_url"] = "https://github.com/acme/rocket/issues/101"
                },
                "/repos/acme/rocket/issues/101/comments" => new JsonArray(),
                _ => new JsonObject()
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
