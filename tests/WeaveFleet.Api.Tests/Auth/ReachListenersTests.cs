using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Api.Auth;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests.Auth;

/// <summary>
/// The desktop app's window signs in on its loopback listener without a key; the listeners it opens for other devices
/// (and the second port for a tunnel) always ask for one. The decision is the listener a connection arrived on, never
/// a header or the remote address.
/// </summary>
public sealed class ReachListenersTests : IDisposable
{
    private const string Key = "desktop-key-0123456789";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-reach-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    // ── The policy, per connection ────────────────────────────────────────────

    [Fact]
    public void Should_grant_auto_auth_on_the_loopback_listener()
    {
        var policy = new LoopbackAuthPolicy("127.0.0.1", port: 6262);

        policy.GrantsAutoAuth(Connection(local: "127.0.0.1", localPort: 6262, remote: "127.0.0.1")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("192.168.1.20", 6262)]
    [InlineData("100.64.90.72", 6262)]
    [InlineData("127.0.0.1", 6263)]
    public void Should_refuse_auto_auth_on_any_other_listener_even_from_a_loopback_remote_address(string local, int localPort)
    {
        var policy = new LoopbackAuthPolicy("127.0.0.1", port: 6262);

        policy.GrantsAutoAuth(Connection(local, localPort, remote: "127.0.0.1")).ShouldBeFalse();
    }

    [Fact]
    public void Should_refuse_auto_auth_on_the_network_listener_whatever_the_forwarding_headers_say()
    {
        var policy = new LoopbackAuthPolicy("127.0.0.1", port: 6262);
        var context = Connection(local: "192.168.1.20", localPort: 6262, remote: "127.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["Forwarded"] = "for=127.0.0.1";
        context.Request.Headers["X-Real-IP"] = "127.0.0.1";

        policy.GrantsAutoAuth(context).ShouldBeFalse();
    }

    [Fact]
    public void Should_not_let_a_header_decide_which_listener_a_connection_arrived_on()
    {
        var policy = new LoopbackAuthPolicy("127.0.0.1", port: 6262);
        var context = Connection(local: "192.168.1.20", localPort: 6262, remote: "192.168.1.31");
        context.Request.Headers.Host = "127.0.0.1:6262";

        policy.GrantsAutoAuth(context).ShouldBeFalse();
    }

    [Fact]
    public void Should_count_a_connection_without_a_local_endpoint_only_while_no_extra_listener_is_open()
    {
        var plain = new LoopbackAuthPolicy("127.0.0.1", port: 6262);
        var inMemory = Connection(local: null, localPort: 0, remote: "127.0.0.1");
        plain.GrantsAutoAuth(inMemory).ShouldBeTrue();

        new ReachStore(DatabasePath()).Save(new ReachSettings(ReachModes.Home));
        var reach = new ReachListeners(new FleetOptions { Port = 6262, DatabasePath = DatabasePath() }, new ReachStore(DatabasePath()), () => [new FoundAddress(IPAddress.Parse("192.168.1.20"), "lan")]);
        var open = new LoopbackAuthPolicy("127.0.0.1", port: 6262, reach: reach);

        open.IsRemoteReachable.ShouldBeTrue();
        open.AllowsLoopbackAutoAuth.ShouldBeTrue();
        open.GrantsAutoAuth(inMemory).ShouldBeFalse();
    }

    // ── Real listeners ────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_open_the_second_port_live_and_ask_it_for_a_key_while_the_window_listener_keeps_signing_in()
    {
        var (primary, own) = TwoFreePorts();
        var options = new FleetOptions { Host = "127.0.0.1", Port = primary, DatabasePath = DatabasePath() };
        var reach = new ReachListeners(options, new ReachStore(options.DatabasePath), () => []);
        await using var app = await StartAsync(options, reach);
        using var client = new HttpClient();

        // Off: only the window's listener is open.
        (await Status(client, primary)).ShouldBe(HttpStatusCode.OK);
        await ShouldRefuseConnection(own);

        (await reach.ApplyAsync(new ReachSettings(ReachModes.Own, "https://fleet.example.com", own), CancellationToken.None)).ShouldBeNull();

        // The window's listener still signs in without a key.
        (await Status(client, primary)).ShouldBe(HttpStatusCode.OK);
        // The new listener never does: no key, and a request claiming to be loopback through headers.
        (await Status(client, own)).ShouldBe(HttpStatusCode.Unauthorized);
        (await Status(client, own, headers: [("X-Forwarded-For", "127.0.0.1"), ("Forwarded", "for=127.0.0.1"), ("X-Real-IP", "127.0.0.1")])).ShouldBe(HttpStatusCode.Unauthorized);
        (await Status(client, own, key: "wrong")).ShouldBe(HttpStatusCode.Unauthorized);
        (await Status(client, own, key: Key)).ShouldBe(HttpStatusCode.OK);

        // Off again closes it.
        (await reach.ApplyAsync(ReachSettings.Off, CancellationToken.None)).ShouldBeNull();
        await ShouldRefuseConnection(own);
        (await Status(client, primary)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_ask_a_network_listener_for_a_key_even_from_this_machine()
    {
        if (OperatingSystem.IsMacOS())
            return; // 127.0.0.2 isn't a loopback alias there by default.

        var (primary, _) = TwoFreePorts();
        var options = new FleetOptions { Host = "127.0.0.1", Port = primary, DatabasePath = DatabasePath() };
        // 127.0.0.2 stands in for an address on the home network: a different local address on the same machine.
        var reach = new ReachListeners(options, new ReachStore(options.DatabasePath), () => [new FoundAddress(IPAddress.Parse("127.0.0.2"), "lan")]);
        await using var app = await StartAsync(options, reach);
        using var client = new HttpClient();

        (await reach.ApplyAsync(new ReachSettings(ReachModes.Home), CancellationToken.None)).ShouldBeNull();

        reach.Listening.Select(address => address.Address.ToString()).ShouldBe(["127.0.0.2"]);
        (await Status(client, primary)).ShouldBe(HttpStatusCode.OK);
        using var spoofed = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.2:{primary}/whoami");
        spoofed.Headers.Add("X-Forwarded-For", "127.0.0.1");
        (await client.SendAsync(spoofed)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var keyed = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.2:{primary}/whoami");
        keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        (await client.SendAsync(keyed)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_say_so_and_keep_the_old_choice_when_nothing_was_found_to_listen_on()
    {
        var (primary, _) = TwoFreePorts();
        var options = new FleetOptions { Host = "127.0.0.1", Port = primary, DatabasePath = DatabasePath() };
        var store = new ReachStore(options.DatabasePath);
        var reach = new ReachListeners(options, store, () => []);
        await using var app = await StartAsync(options, reach);

        var error = await reach.ApplyAsync(new ReachSettings(ReachModes.TailscaleAndHome), CancellationToken.None);

        error.ShouldNotBeNull();
        reach.Settings.Mode.ShouldBe(ReachModes.Off);
        store.Get().Mode.ShouldBe(ReachModes.Off);
    }

    [Fact]
    public void Should_keep_the_choice_across_restarts()
    {
        var options = new FleetOptions { Host = "127.0.0.1", Port = 6262, DatabasePath = DatabasePath() };
        var address = new FoundAddress(IPAddress.Parse("192.168.1.20"), "lan");
        new ReachStore(options.DatabasePath).Save(new ReachSettings(ReachModes.Home));

        var restarted = new ReachListeners(options, new ReachStore(options.DatabasePath), () => [address]);

        restarted.Settings.Mode.ShouldBe(ReachModes.Home);
        restarted.Listening.ShouldBe([address]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"mode\":\"everyone\"}")]
    public void Should_start_off_when_the_saved_choice_is_unreadable(string content)
    {
        var path = DatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(new ReachStore(path).FilePath, content);

        new ReachStore(path).Get().Mode.ShouldBe(ReachModes.Off);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string DatabasePath() => Path.Combine(_directory, "fleet.db");

    private static DefaultHttpContext Connection(string? local, int localPort, string remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = local is null ? null : IPAddress.Parse(local);
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        return context;
    }

    /// <summary>
    /// A real Kestrel host reading its endpoints from <paramref name="reach"/>, with one route that answers the way the
    /// bearer handler does: 200 for a request the policy lets in or one carrying the key, 401 otherwise.
    /// </summary>
    private static async Task<WebApplication> StartAsync(FleetOptions options, ReachListeners reach)
    {
        var policy = new LoopbackAuthPolicy(options.Host, port: options.Port, reach: reach);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        ((IConfigurationBuilder)builder.Configuration).Add(reach.Source);
        builder.WebHost.ConfigureKestrel((context, kestrel) => kestrel.Configure(context.Configuration.GetSection(ReachListeners.Section), reloadOnChange: true));
        var app = builder.Build();
        app.MapGet("/whoami", (HttpContext context) =>
        {
            var presented = context.Request.Headers.Authorization.ToString();
            return policy.GrantsAutoAuth(context) || presented == $"Bearer {Key}" ? Results.Ok() : Results.Unauthorized();
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, int port, string? key = null, (string Name, string Value)[]? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/whoami");
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var (name, value) in headers ?? [])
            request.Headers.Add(name, value);
        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task ShouldRefuseConnection(int port)
    {
        // Closing a listener takes Kestrel a moment after the reload.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
            }
            catch (SocketException)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Port {port} still accepts connections.");
    }

    private static (int First, int Second) TwoFreePorts()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
    }
}
