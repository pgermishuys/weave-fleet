using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

public sealed class GitHubReviewThreadsParsingTests
{
    [Fact]
    public void build_review_threads_response_returns_empty_when_null()
    {
        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(null);

        result.UnresolvedCount.ShouldBe(0);
        result.Threads.ShouldBeEmpty();
    }

    [Fact]
    public void build_review_threads_response_returns_empty_when_no_threads()
    {
        var response = JsonNode.Parse("""
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "nodes": []
                }
              }
            }
          }
        }
        """);

        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(response);

        result.UnresolvedCount.ShouldBe(0);
        result.Threads.ShouldBeEmpty();
    }

    [Fact]
    public void build_review_threads_response_counts_only_unresolved_non_outdated()
    {
        var response = JsonNode.Parse("""
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "nodes": [
                    {
                      "id": "PRRT_1",
                      "isResolved": false,
                      "isOutdated": false,
                      "path": "src/Foo.cs",
                      "line": 10,
                      "comments": { "nodes": [{ "id": "IC_1", "databaseId": 100, "body": "fix this", "author": { "login": "rev" }, "createdAt": "2026-01-01T00:00:00Z", "url": "https://github.com/c/1" }] }
                    },
                    {
                      "id": "PRRT_2",
                      "isResolved": true,
                      "isOutdated": false,
                      "path": "src/Bar.cs",
                      "line": 20,
                      "comments": { "nodes": [{ "id": "IC_2", "databaseId": 200, "body": "resolved", "author": { "login": "rev" }, "createdAt": "2026-01-01T00:00:00Z", "url": "https://github.com/c/2" }] }
                    },
                    {
                      "id": "PRRT_3",
                      "isResolved": false,
                      "isOutdated": true,
                      "path": "src/Baz.cs",
                      "line": 30,
                      "comments": { "nodes": [{ "id": "IC_3", "databaseId": 300, "body": "outdated", "author": { "login": "rev" }, "createdAt": "2026-01-01T00:00:00Z", "url": "https://github.com/c/3" }] }
                    },
                    {
                      "id": "PRRT_4",
                      "isResolved": false,
                      "isOutdated": false,
                      "path": "src/Qux.cs",
                      "line": 40,
                      "comments": { "nodes": [{ "id": "IC_4", "databaseId": 400, "body": "also fix", "author": { "login": "rev2" }, "createdAt": "2026-01-02T00:00:00Z", "url": "https://github.com/c/4" }] }
                    }
                  ]
                }
              }
            }
          }
        }
        """);

        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(response);

        result.UnresolvedCount.ShouldBe(2); // PRRT_1 and PRRT_4 only
        result.Threads.Count.ShouldBe(4);   // all 4 threads returned
    }

    [Fact]
    public void build_review_threads_response_maps_comment_fields_correctly()
    {
        var response = JsonNode.Parse("""
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "nodes": [
                    {
                      "id": "PRRT_abc",
                      "isResolved": false,
                      "isOutdated": false,
                      "path": "src/Service.cs",
                      "line": 42,
                      "comments": {
                        "nodes": [
                          {
                            "id": "IC_xyz",
                            "databaseId": 12345,
                            "body": "Use CultureInfo.InvariantCulture here",
                            "author": { "login": "reviewer" },
                            "createdAt": "2026-05-17T10:00:00Z",
                            "url": "https://github.com/org/repo/pull/1#discussion_r12345"
                          }
                        ]
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """);

        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(response);

        result.Threads.Count.ShouldBe(1);
        var thread = result.Threads[0];
        thread.ThreadNodeId.ShouldBe("PRRT_abc");
        thread.IsResolved.ShouldBeFalse();
        thread.Path.ShouldBe("src/Service.cs");
        thread.Line.ShouldBe(42);

        thread.Comments.Count.ShouldBe(1);
        var comment = thread.Comments[0];
        comment.Id.ShouldBe("IC_xyz");
        comment.DatabaseId.ShouldBe(12345);
        comment.Body.ShouldBe("Use CultureInfo.InvariantCulture here");
        comment.AuthorLogin.ShouldBe("reviewer");
        comment.Url.ShouldContain("discussion_r12345");
    }

    [Fact]
    public void build_review_threads_response_handles_malformed_data_gracefully()
    {
        var response = JsonNode.Parse("""
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "nodes": [
                    {
                      "isResolved": false,
                      "isOutdated": false,
                      "comments": { "nodes": [] }
                    }
                  ]
                }
              }
            }
          }
        }
        """);

        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(response);

        result.UnresolvedCount.ShouldBe(1);
        result.Threads.Count.ShouldBe(1);
        result.Threads[0].ThreadNodeId.ShouldBe(string.Empty);
        result.Threads[0].Path.ShouldBe(string.Empty);
        result.Threads[0].Line.ShouldBeNull();
        result.Threads[0].Comments.ShouldBeEmpty();
    }

    [Fact]
    public void build_review_threads_response_handles_missing_data_path()
    {
        var response = JsonNode.Parse("""{ "errors": [{ "message": "Not found" }] }""");

        var result = GitHubEndpointMappings.BuildReviewThreadsResponse(response);

        result.UnresolvedCount.ShouldBe(0);
        result.Threads.ShouldBeEmpty();
    }
}
