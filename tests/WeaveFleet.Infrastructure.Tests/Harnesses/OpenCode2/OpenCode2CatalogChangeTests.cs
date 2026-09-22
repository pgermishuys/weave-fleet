using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// V2 rebuilds a folder's agents, models and commands whenever a file it watches changes, and says so with
/// <c>*.updated</c> events (kind and folder, never what). The server tells Fleet once per change, for folders Fleet uses,
/// and not while a folder is still loading. Event shapes are from 2.0.9.
/// </summary>
public sealed class OpenCode2CatalogChangeTests
{
    private const string Folder = "/work/rocket";
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(100);

    // The rounds V2 sends for one agent file written in a loaded folder (2.0.9 sends two or three).
    private static readonly string[] AgentFileWritten =
        ["config.updated", "agent.updated", "skill.updated", "provider.updated", "model.updated", "command.updated", "agent.updated", "agent.updated"];

    [Fact]
    public async Task A_change_to_a_loaded_folder_is_told_once_V2_goes_quiet()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);

        foreach (var type in AgentFileWritten)
            server.Route(Updated(type, Folder));
        await Task.Delay(Quiet * 4);

        changes.ShouldBe([Folder]);
    }

    [Fact]
    public async Task Events_that_keep_coming_are_told_once_they_stop()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);

        for (var i = 0; i < 5; i++)
        {
            server.Route(Updated("agent.updated", Folder));
            await Task.Delay(Quiet / 2);
        }
        changes.ShouldBeEmpty();

        await Task.Delay(Quiet * 4);
        changes.ShouldBe([Folder]);
    }

    [Fact]
    public async Task A_later_change_is_told_again()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);

        server.Route(Updated("agent.updated", Folder));
        await Task.Delay(Quiet * 4);
        server.Route(Updated("model.updated", Folder));
        await Task.Delay(Quiet * 4);

        changes.ShouldBe([Folder, Folder]);
    }

    [Fact]
    public async Task A_folder_loading_is_not_a_change()
    {
        // Right after a session starts in a new folder V2 loads it: the whole catalog arrives, then more rounds.
        var changes = new ConcurrentQueue<string>();
        await using var server = Server(changes, settle: TimeSpan.FromSeconds(30));
        var loading = server.LoadLocationAsync(Folder, CancellationToken.None);
        foreach (var type in LoadEvents.Concat(AgentFileWritten))
            server.Route(Updated(type, Folder));
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(Quiet * 4);

        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Events_for_a_folder_V2_has_not_loaded_are_not_a_change()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);

        server.Route(Updated("agent.updated", "/work/comet"));
        await Task.Delay(Quiet * 4);

        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_Fleet_never_asked_about_is_not_told()
    {
        // V2 loads its own working folder too, and sends its events; no session runs there.
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);
        foreach (var type in LoadEvents)
            server.Route(Updated(type, "/home/you"));

        server.Route(Updated("agent.updated", "/home/you"));
        await Task.Delay(Quiet * 4);

        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_a_session_runs_in_is_told_and_names_its_sessions()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = Server(changes);
        server.Attach("ses_1", new Sink("fleet-1", Folder + "/"));
        server.Attach("ses_2", new Sink("fleet-2", "/work/comet"));
        foreach (var type in LoadEvents)
            server.Route(Updated(type, Folder));

        server.Route(Updated("command.updated", Folder));
        await Task.Delay(Quiet * 4);

        changes.ShouldBe([Folder]);
        server.SessionsIn(Folder).ShouldBe(["fleet-1"]);
    }

    [Theory]
    [InlineData("skill.updated")]
    [InlineData("plugin.updated")]
    [InlineData("websearch.updated")]
    [InlineData("reference.updated")]
    public async Task Rebuilds_that_do_not_change_the_catalog_are_not_told(string type)
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await LoadedServerAsync(changes);

        server.Route(Updated(type, Folder));
        await Task.Delay(Quiet * 4);

        changes.ShouldBeEmpty();
    }

    private static readonly string[] LoadEvents = ["integration.updated", "provider.updated", "model.updated", "agent.updated", "command.updated"];

    /// <summary>A server that has loaded <see cref="Folder"/> for a catalog read, with no settle time after it.</summary>
    private static async Task<OpenCode2Server> LoadedServerAsync(ConcurrentQueue<string> changes)
    {
        var server = Server(changes);
        var loading = server.LoadLocationAsync(Folder, CancellationToken.None);
        foreach (var type in LoadEvents)
            server.Route(Updated(type, Folder));
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        return server;
    }

    private static OpenCode2Server Server(ConcurrentQueue<string> changes, TimeSpan? settle = null)
        => new("local-user", OpenCode2Fixtures.ClientServing("", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), "token", process: null, NullLogger.Instance)
        {
            CatalogChanged = (_, directory) => changes.Enqueue(directory),
            CatalogChangeQuietTime = Quiet,
            LocationSettleTime = settle ?? TimeSpan.Zero,
        };

    private static OpenCode2Event Updated(string type, string directory) => new()
    {
        Id = $"evt_{Guid.NewGuid():N}",
        Type = type,
        Location = new OpenCode2EventLocation { Directory = directory },
        Data = JsonDocument.Parse("{}").RootElement.Clone(),
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
