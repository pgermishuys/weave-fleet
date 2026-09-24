using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// V2 (2.0.15) unloads a folder after an hour without a session event: it interrupts the turns still running there
/// (a question waiting for the user among them) and kills the folder's background shells. The server keeps a quiet
/// folder loaded while V2 still has work in it, by writing a session's permission rules back unchanged, which V2
/// records as a <c>session.permissions</c> event.
/// </summary>
public sealed class OpenCode2KeepLoadedTests
{
    private const string Folder = "/work/rocket";
    private const string Rules = """[{"action":"*","resource":"*","effect":"allow"}]""";

    [Fact]
    public async Task A_quiet_folder_with_a_turn_running_is_kept_loaded()
    {
        // A turn waiting on the user's answer to a question is running, as far as V2 is concerned.
        var api = Api(active: ["ses_1"]);
        await using var server = Server(api);
        server.Attach("ses_1", new Sink("fleet-1", Folder));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBe([Folder]);
        api.Requests.Where(r => r.Method == HttpMethod.Patch).ShouldHaveSingleItem()
            .ShouldBe((HttpMethod.Patch, "/api/session/ses_1", $$"""{"permissions":{{Rules}}}"""));
    }

    [Fact]
    public async Task A_quiet_folder_with_a_background_shell_is_kept_loaded()
    {
        // The turn that backgrounded the shell has ended; the shell runs on.
        var api = Api(active: [], shells: [OpenCode2Fixtures.Shell("running", sessionId: "ses_1")]);
        await using var server = Server(api);
        server.Attach("ses_1", new Sink("fleet-1", Folder));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBe([Folder]);
        api.Requests.Count(r => r.Method == HttpMethod.Patch).ShouldBe(1);
    }

    [Fact]
    public async Task A_quiet_folder_with_nothing_running_is_left_to_unload()
    {
        var api = Api(active: [], shells: [OpenCode2Fixtures.Shell("exited", sessionId: "ses_1")]);
        await using var server = Server(api);
        server.Attach("ses_1", new Sink("fleet-1", Folder));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBeEmpty();
        api.Requests.ShouldNotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task A_folder_with_recent_session_events_is_not_asked_about()
    {
        var api = Api(active: ["ses_1"]);
        await using var server = Server(api, quietLimit: TimeSpan.FromMinutes(40));
        server.Attach("ses_1", new Sink("fleet-1", Folder));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBeEmpty();
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_without_rules_of_its_own_is_passed_over_for_one_with_them()
    {
        // A subagent's child session has no rules of its own; Fleet sets them on the sessions it creates.
        var api = Api(active: ["ses_child"], withoutRules: ["ses_child"]);
        await using var server = Server(api);
        server.Attach("ses_child", new Sink("fleet-child", Folder));
        server.Attach("ses_1", new Sink("fleet-1", Folder + "/"));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBe([Folder]);
        api.Requests.Where(r => r.Method == HttpMethod.Patch).Select(r => r.Path).ShouldBe(["/api/session/ses_1"]);
    }

    [Fact]
    public async Task A_folder_V2_cannot_answer_for_is_tried_again_next_round()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var server = Server(api);
        server.Attach("ses_1", new Sink("fleet-1", Folder));

        var kept = await server.KeepBusyFoldersLoadedAsync(CancellationToken.None);

        kept.ShouldBeEmpty();
    }

    /// <summary>V2 with the given sessions running and background shells in <see cref="Folder"/>.</summary>
    private static StubHandler Api(string[] active, string[]? shells = null, string[]? withoutRules = null)
        => new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/session/active")
                return Json("{\"data\":{" + string.Join(',', active.Select(id => JsonSerializer.Serialize(id) + """:{"type":"running"}""")) + "}}");
            if (path == "/api/debug/location")
                return Json($$"""[{"directory":{{JsonSerializer.Serialize(Folder)}}}]""");
            if (path == "/api/shell")
                return Json($$"""{"location":{"directory":{{JsonSerializer.Serialize(Folder)}}},"data":[{{string.Join(',', shells ?? [])}}]}""");
            if (path.StartsWith("/api/session/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var id = path["/api/session/".Length..];
                var rules = withoutRules?.Contains(id) == true ? "" : ",\"permissions\":" + Rules;
                return Json("{\"data\":{\"id\":" + JsonSerializer.Serialize(id)
                    + ",\"location\":{\"directory\":" + JsonSerializer.Serialize(Folder) + "}" + rules + "}}");
            }
            if (path.StartsWith("/api/session/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static OpenCode2Server Server(StubHandler api, TimeSpan? quietLimit = null)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance)
        {
            FolderQuietLimit = quietLimit ?? TimeSpan.Zero,
        };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class Sink(string fleetSessionId, string directory) : IOpenCode2EventSink
    {
        public OpenCode2SessionContext Context { get; } = new(fleetSessionId, "local-user", directory, null, null);

        public void OnEvent(OpenCode2Event evt)
        {
        }

        public Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct) => Task.CompletedTask;

        public void OnServerStopped()
        {
        }
    }
}
