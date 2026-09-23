using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The designer's endpoints: checking as the user edits, and New, open and save in a repository's .weave/workflows.
/// Over HTTP, so the source-generated JSON is what the client gets.
/// </summary>
public sealed class WorkflowEditEndpointTests : IAsyncDisposable
{
    private static readonly string[] Done = ["done"];

    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly HttpClient _client;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-api-workflows-{Guid.NewGuid():N}");
    private readonly string _repo;

    public WorkflowEditEndpointTests()
    {
        _client = _factory.CreateClient();
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task TurnOnAsync()
    {
        (await _client.PutAsJsonAsync("/api/preferences/Workflows", new { value = "true" })).EnsureSuccessStatusCode();
        await GitAsync(_repo, "init", "-q");
        (await _client.PostAsJsonAsync("/api/workspace-roots", new { path = _root })).IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_gives_each_error_its_line_and_step_for_text_and_for_a_draft()
    {
        await TurnOnAsync();

        var text = await PostAsync("/api/workflows/check", new
        {
            text = "# ours\nname: Deps\nsteps:\n  - id: a\n    title: A\n    model: fast\n    prompt: A.\n    outcomes: [done, again]\n    on: { again: a }\n",
        });
        var error = text.GetProperty("errors").EnumerateArray().ShouldHaveSingleItem();
        error.GetProperty("line").GetInt32().ShouldBe(9);
        error.GetProperty("step").GetInt32().ShouldBe(0);
        error.GetProperty("message").GetString().ShouldBe("a sends work back to an earlier step, so it needs a max: how many times it may do that in a run.");
        text.GetProperty("comments").EnumerateArray().ShouldHaveSingleItem().GetProperty("text").GetString().ShouldBe("ours");
        var draft = text.GetProperty("draft");
        draft.GetProperty("name").GetString().ShouldBe("Deps");
        draft.GetProperty("steps")[0].GetProperty("prompt").GetString().ShouldBe("A.");

        // The designer sends only what it has; what it leaves out is empty, not null.
        var written = await PostAsync("/api/workflows/check", new
        {
            draft = new
            {
                name = "Deps",
                steps = new object[]
                {
                    new { id = "a", title = "A", kind = "agent", model = "fast", prompt = "A.", outcomes = Done },
                    new { id = "ok", title = "OK?", kind = "you", ask = "Keep it?", choices = new[] { new { label = "Keep", to = "end", note = false } } },
                },
            },
        });
        written.GetProperty("errors").GetArrayLength().ShouldBe(0);
        written.GetProperty("text").GetString().ShouldBe("""
            name: Deps
            starts-from: sentence
            runs-in: new-worktree
            steps:
              - id: a
                title: A
                model: fast
                prompt: |
                  A.
                outcomes: [done]

              - id: ok
                title: OK?
                you: Keep it?
                choices:
                  Keep: end

            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task New_open_and_save_work_in_the_repository_and_a_stale_save_is_a_conflict()
    {
        await TurnOnAsync();

        var created = await PostAsync("/api/workflows/files", new { directory = _repo, name = "Tidy up a flaky test" });
        created.GetProperty("workflowId").GetString().ShouldBe("repo:tidy-up-a-flaky-test");
        created.GetProperty("file").GetString().ShouldBe(".weave/workflows/tidy-up-a-flaky-test.yaml");
        var hash = created.GetProperty("hash").GetString();
        var path = Path.Combine(_repo, ".weave", "workflows", "tidy-up-a-flaky-test.yaml");
        File.Exists(path).ShouldBeTrue();

        var taken = await _client.PostAsJsonAsync("/api/workflows/files", new { directory = _repo, name = "Tidy up a flaky test" });
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var duplicate = await PostAsync("/api/workflows/files", new { directory = _repo, name = "Build a feature, our way", workflowId = "builtin:build-a-feature" });
        duplicate.GetProperty("check").GetProperty("draft").GetProperty("steps").GetArrayLength().ShouldBe(8);

        var opened = await PostAsync("/api/workflows/files/open", new { directory = _repo, workflowId = "repo:tidy-up-a-flaky-test" });
        opened.GetProperty("hash").GetString().ShouldBe(hash);

        var text = opened.GetProperty("check").GetProperty("text").GetString()!.Replace("Do the work", "Do the work # mine");
        var saved = await _client.PutAsJsonAsync("/api/workflows/files", new { directory = _repo, workflowId = "repo:tidy-up-a-flaky-test", hash, text });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await File.ReadAllTextAsync(path)).ShouldBe(text);

        // The old hash again: the file changed since.
        var stale = await _client.PutAsJsonAsync("/api/workflows/files", new { directory = _repo, workflowId = "repo:tidy-up-a-flaky-test", hash, text });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var builtIn = await _client.PutAsJsonAsync("/api/workflows/files", new { directory = _repo, workflowId = "builtin:build-a-feature", hash, text, force = true });
        builtIn.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task With_workflows_off_the_editing_routes_are_not_there()
    {
        var response = await _client.PostAsJsonAsync("/api/workflows/check", new { text = "name: x" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
        return json;
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0);
    }
}
