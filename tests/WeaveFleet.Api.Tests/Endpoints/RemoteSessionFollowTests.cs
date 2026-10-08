using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Machines;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// A session on hangar waits to hear from one on falcon (agent hand-off). Hangar follows that session over the hub
/// connection it keeps to falcon: its reply and going idle come as falcon's own events, and a turn that ended before
/// hangar was listening is asked about when it starts. Two Fleets in one process, hangar's calls routed to falcon.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class RemoteSessionFollowTests
{
    private const string FalconUrl = "https://falcon.tail9c2e.ts.net";
    private const string Session = "sess-falcon";
    private const string Owner = "local-user";

    [Fact]
    public async Task Its_reply_and_going_idle_arrive_as_falcons_events_and_tell_the_session_waiting()
    {
        var broadcaster = new InMemoryEventBroadcaster();
        var messages = new Messages(replied: false);
        await using var falcon = Falcon(broadcaster, messages.Proxy);
        var sender = new CapturingSender();
        await using var hangar = Hangar(falcon, sender);
        await SeedAsync(falcon);
        var falconId = await AddAsync(hangar, falcon);

        Wait(hangar, falconId);
        // Subscribing reads the session (its snapshot, then the catch-up), so once it has been read the events flow.
        await WaitForAsync(() => messages.Reads > 0, "hangar following the session on falcon");

        await broadcaster.BroadcastAsync($"session:{Session}", "message.updated", Json("""
            {"info":{"id":"reply-1","role":"assistant","sessionID":"sess-falcon","parentID":"msg-1","time":{"created":0}}}
            """), eventId: 1, domainEvent: null, userId: null, ct: CancellationToken.None);
        await broadcaster.BroadcastAsync($"session:{Session}", "session.idled", Json("""{"sessionId":"sess-falcon"}"""),
            eventId: 2, domainEvent: null, userId: null, ct: CancellationToken.None);

        await WaitForAsync(() => sender.Read.Count > 0, "the update");
        var watch = sender.Read.ShouldHaveSingleItem();
        watch.MessageId.ShouldBe("msg-1");
        watch.Machine!.Id.ShouldBe(falconId);
    }

    [Fact]
    public async Task A_reply_that_ended_before_hangar_listened_is_delivered_without_waiting_for_an_event()
    {
        await using var falcon = Falcon(new InMemoryEventBroadcaster(), new Messages(replied: true).Proxy);
        var sender = new CapturingSender();
        await using var hangar = Hangar(falcon, sender);
        await SeedAsync(falcon);
        var falconId = await AddAsync(hangar, falcon);

        Wait(hangar, falconId);

        await WaitForAsync(() => sender.Read.Count > 0, "the update");
        sender.Read.ShouldHaveSingleItem().MessageId.ShouldBe("msg-1");
    }

    /// <summary>A session on hangar asks to hear when falcon's session answers msg-1, as the hand-off bridge does.</summary>
    private static void Wait(ApiWebApplicationFactory hangar, string falconId)
    {
        hangar.Services.GetRequiredService<SessionUpdates>().Watch(
            new SessionUpdateWatch("sess-hangar", Session, Owner, "msg-1", new SessionMessageMachine(falconId, "falcon"), "Run the tests"));
        hangar.Services.GetRequiredService<IRemoteSessionEvents>().Follow(falconId, Session);
    }

    private static ApiWebApplicationFactory Falcon(InMemoryEventBroadcaster broadcaster, FakeSessionMessageProxy messages) => new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        host: "0.0.0.0",
        configureTestServices: services =>
        {
            services.AddSingleton<IEventBroadcaster>(broadcaster);
            services.RemoveAll<ISessionMessageProxy>();
            services.AddSingleton<ISessionMessageProxy>(messages);
        });

    private static ApiWebApplicationFactory Hangar(ApiWebApplicationFactory falcon, CapturingSender sender) => new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        host: "0.0.0.0",
        configureTestServices: services =>
        {
            services.AddHttpClient(RemoteMachineService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => falcon.Server.CreateHandler());
            services.RemoveAll<ISessionUpdateSender>();
            services.AddSingleton<ISessionUpdateSender>(sender);
        });

    /// <summary>Hangar keeps falcon in its list, allowed for hand-offs, and its hub connection goes to falcon's test server.</summary>
    private static async Task<string> AddAsync(ApiWebApplicationFactory hangar, ApiWebApplicationFactory falcon)
    {
        hangar.Services.GetRequiredService<RemoteMachineWatcher>().Handler = falcon.Server.CreateHandler();
        using var owner = hangar.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(hangar));
        var added = await (await owner.PostAsJsonAsync("/api/machines", new { baseUrl = FalconUrl, token = Token(falcon) })).Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("id").GetString()!;
        (await owner.PutAsJsonAsync($"/api/machines/{id}", new { agentsAllowed = true })).EnsureSuccessStatusCode();
        return id;
    }

    private static string Token(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    /// <summary>Falcon's session: the message from hangar, and its reply once there is one. Counts the reads.</summary>
    private sealed class Messages
    {
        private int _reads;

        public Messages(bool replied)
        {
            List<HarnessMessage> messages = [Message("msg-1", "user", "<fleet-session-message …>")];
            if (replied)
                messages.Add(Message("reply-1", "assistant", "2 failed."));
            Proxy = new FakeSessionMessageProxy
            {
                GetMessagesBehavior = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref _reads);
                    return Task.FromResult(new MessagePage(messages, HasMore: false));
                },
            };
        }

        public FakeSessionMessageProxy Proxy { get; }

        public int Reads => Volatile.Read(ref _reads);
    }

    private static HarnessMessage Message(string id, string role, string text)
        => new() { Id = id, Role = role, Parts = [new TextPart(text)], Timestamp = DateTimeOffset.UnixEpoch };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task SeedAsync(ApiWebApplicationFactory falcon)
    {
        using var scope = falcon.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-10-08T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-08T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Session}', 'ws-1', 'inst-1', 'oc-1', 'Run the tests', 'active', '/ws',
                    'running', 'active', '2026-10-08T10:00:00+00:00', '{Owner}');
            """);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private sealed class CapturingSender : ISessionUpdateSender
    {
        private readonly List<SessionUpdateWatch> _read = [];

        public IReadOnlyList<SessionUpdateWatch> Read
        {
            get
            {
                lock (_read)
                    return [.. _read];
            }
        }

        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
        {
            lock (_read)
                _read.Add(watch);
            return Task.FromResult<SessionUpdate?>(null);
        }

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct) => Task.FromResult(true);
    }
}
