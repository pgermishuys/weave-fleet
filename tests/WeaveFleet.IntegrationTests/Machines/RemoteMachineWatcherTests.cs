using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Push;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Machines;

namespace WeaveFleet.IntegrationTests.Machines;

/// <summary>
/// A phone's home machine (hangar) listening to another machine (falcon) and pushing falcon's notifications to the
/// phone, tagged as falcon's. Two Fleets in one process; hangar reaches falcon's hub through falcon's test server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RemoteMachineWatcherTests
{
    [Fact]
    public async Task A_notification_on_another_machine_is_pushed_by_home_as_that_machines()
    {
        await using var falcon = new FleetHost();
        var sender = new RecordingSender();
        await using var hangar = new FleetHost(sender);
        await SubscribeAsync(hangar);
        hangar.Services.GetRequiredService<RemoteMachineWatcher>().Handler = falcon.Server.CreateHandler();

        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(falcon.MachineId, "falcon", "http://falcon.test", falcon.Token, "linux", null)]);
        await WaitForAsync(async () => (await StatusAsync(hangar, falcon.MachineId)) == RemoteMachineStatuses.Online);

        await falcon.Services.GetServices<ISessionNotificationSink>().OfType<BroadcastNotificationSink>().Single().HandleAsync(
            new SessionNotificationPayload
            {
                SessionId = "falcon-session",
                Reason = SessionNotificationReasons.NeedsYou,
                Kind = SessionNotificationKinds.Permission,
                RequestId = "perm-1",
                MachineId = falcon.MachineId,
                MachineName = "falcon",
                Title = "Per-device tokens for machines",
                Body = "Wants to run dotnet test",
            },
            "local-user",
            CancellationToken.None);

        await WaitForAsync(() => Task.FromResult(sender.Sent.Count > 0));
        var payload = JsonDocument.Parse(sender.Sent.Single()).RootElement;
        payload.GetProperty("machineId").GetString().ShouldBe(falcon.MachineId);
        payload.GetProperty("machineName").GetString().ShouldBe("falcon");
        payload.GetProperty("sessionId").GetString().ShouldBe("falcon-session");
        payload.GetProperty("kind").GetString().ShouldBe("permission");
        payload.GetProperty("url").GetString().ShouldBe($"/phone/s/{falcon.MachineId}/falcon-session?ask=perm-1");
    }

    [Fact]
    public async Task An_older_machine_without_kinds_reads_as_needs_you()
    {
        await using var falcon = new FleetHost();
        var sender = new RecordingSender();
        await using var hangar = new FleetHost(sender);
        await SubscribeAsync(hangar);
        hangar.Services.GetRequiredService<RemoteMachineWatcher>().Handler = falcon.Server.CreateHandler();
        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(falcon.MachineId, "falcon", "http://falcon.test", falcon.Token, "linux", null)]);
        await WaitForAsync(async () => (await StatusAsync(hangar, falcon.MachineId)) == RemoteMachineStatuses.Online);

        await falcon.Services.GetServices<ISessionNotificationSink>().OfType<BroadcastNotificationSink>().Single().HandleAsync(
            new SessionNotificationPayload { SessionId = "s", Reason = SessionNotificationReasons.NeedsYou, Title = "Old", Body = "Waiting on your answer." },
            "local-user",
            CancellationToken.None);

        await WaitForAsync(() => Task.FromResult(sender.Sent.Count > 0));
        var payload = JsonDocument.Parse(sender.Sent.Single()).RootElement;
        payload.GetProperty("kind").GetString().ShouldBe("permission");
        payload.GetProperty("machineId").GetString().ShouldBe(falcon.MachineId);
        payload.GetProperty("machineName").GetString().ShouldBe("falcon");
    }

    [Fact]
    public async Task A_machine_cannot_speak_for_another_and_its_name_comes_from_the_list()
    {
        await using var falcon = new FleetHost();
        var sender = new RecordingSender();
        await using var hangar = new FleetHost(sender);
        await SubscribeAsync(hangar);
        hangar.Services.GetRequiredService<RemoteMachineWatcher>().Handler = falcon.Server.CreateHandler();
        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(falcon.MachineId, "falcon", "http://falcon.test", falcon.Token, "linux", null)]);
        await WaitForAsync(async () => (await StatusAsync(hangar, falcon.MachineId)) == RemoteMachineStatuses.Online);
        var sink = falcon.Services.GetServices<ISessionNotificationSink>().OfType<BroadcastNotificationSink>().Single();

        await sink.HandleAsync(
            new SessionNotificationPayload { SessionId = "s", Reason = SessionNotificationReasons.NeedsYou, Kind = SessionNotificationKinds.Permission, MachineId = "kestrel-id", MachineName = "kestrel", Title = "Spoofed", Body = "Allow?" },
            "local-user",
            CancellationToken.None);
        await sink.HandleAsync(
            new SessionNotificationPayload { SessionId = "s", Reason = SessionNotificationReasons.NeedsYou, Kind = SessionNotificationKinds.Permission, MachineId = falcon.MachineId, MachineName = "hangar", Title = "Real", Body = "Allow?" },
            "local-user",
            CancellationToken.None);

        await WaitForAsync(() => Task.FromResult(sender.Sent.Count > 0));
        await Task.Delay(200);
        var payload = JsonDocument.Parse(sender.Sent.Single()).RootElement;
        payload.GetProperty("title").GetString().ShouldBe("Real");
        payload.GetProperty("machineId").GetString().ShouldBe(falcon.MachineId);
        payload.GetProperty("machineName").GetString().ShouldBe("falcon");
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_is_marked_unreachable()
    {
        var sender = new RecordingSender();
        await using var hangar = new FleetHost(sender);

        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine("ghost-id", "ghost", "http://127.0.0.1:9", "ghost-token-0123456789", "linux", null)]);

        await WaitForAsync(async () => (await StatusAsync(hangar, "ghost-id")) == RemoteMachineStatuses.Unreachable);
    }

    [Fact]
    public async Task A_replaced_token_marks_the_machine_unauthorized()
    {
        await using var falcon = new FleetHost();
        await using var hangar = new FleetHost(new RecordingSender());
        hangar.Services.GetRequiredService<RemoteMachineWatcher>().Handler = falcon.Server.CreateHandler();

        await hangar.Services.GetRequiredService<RemoteMachineService>().ImportAsync(
            [new ImportedMachine(falcon.MachineId, "falcon", "http://falcon.test", "an-old-token-0123456789", "linux", null)]);

        await WaitForAsync(async () => (await StatusAsync(hangar, falcon.MachineId)) == RemoteMachineStatuses.Unauthorized);
    }

    private static async Task SubscribeAsync(FleetHost host) =>
        await host.Services.GetRequiredService<IPushSubscriptionRepository>().UpsertAsync(new PushSubscriptionRecord
        {
            Id = "phone",
            Endpoint = "https://push.example/phone",
            P256dh = "k",
            Auth = "a",
            Kinds = SessionNotificationKinds.All,
            QuietWhenDesk = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private static async Task<string?> StatusAsync(FleetHost host, string machineId) =>
        (await host.Services.GetRequiredService<IRemoteMachineRepository>().GetAsync(machineId))?.Status;

    private static async Task WaitForAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        (await condition()).ShouldBeTrue("timed out waiting");
    }

    private sealed class RecordingSender : IPushSender
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        public string Channel => "webpush";

        public Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, CancellationToken cancellationToken)
        {
            lock (_sent)
                _sent.Add(message.Payload);
            return Task.FromResult(new PushSendResult(PushSendOutcome.Delivered));
        }
    }

    /// <summary>A local-mode Fleet with token auth on a test server, its own database and machine identity.</summary>
    private sealed class FleetHost(IPushSender? sender = null) : WebApplicationFactory<Program>
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("fleet-watcher-").FullName;

        public string MachineId => Services.GetRequiredService<MachineIdentityStore>().Get().Id;

        public string Token => Services.GetRequiredService<ILocalTokenAuthService>().Token;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // No Bun for the mod host, whatever this machine has installed: tests never start a real one here.
            builder.ConfigureTestServices(services => services.AddSingleton<WeaveFleet.Application.Mods.Host.IModHostBun, WeaveFleet.Testing.Fakes.NoModHostBun>());
            var webRoot = Path.Combine(_dir, "wwwroot");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html></html>");
            builder.UseEnvironment("Testing");
            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
            builder.UseSetting("Fleet:DatabasePath", Path.Combine(_dir, "fleet.db"));
            builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.Combine(_dir, "analytics.db"));
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
            builder.UseSetting("Fleet:Port", "0");
            builder.UseSetting("Fleet:Host", "127.0.0.1");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "true");
            builder.UseSetting("Fleet:Auth:RequireToken", "true");
            if (sender is not null)
                builder.ConfigureTestServices(services => services.AddSingleton(sender));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
