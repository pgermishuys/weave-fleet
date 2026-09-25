using System.IO.Pipelines;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Api.Browser;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests.Browser;

public sealed class PreviewGatewayTests
{
    /// <summary>
    /// The start of what SDK 10.0.112's <c>aspnetcore-browser-refresh.js</c> serves, with its template filled in
    /// (<c>'{{hostString}}'</c> becomes the refresh servers). If an SDK changes this, the rewrite stops matching
    /// and this test says so.
    /// </summary>
    private const string Sdk10RefreshScript = """
        setTimeout(async function () {
          const hotReloadActiveKey = '_dotnet_watch_hot_reload_active';
          // Ensure we only try to connect once, even if the script is both injected and manually inserted
          const scriptInjectedSentinel = '_dotnet_watch_ws_injected';
          if (window.hasOwnProperty(scriptInjectedSentinel)) {
            return;
          }
          window[scriptInjectedSentinel] = true;

          // dotnet-watch browser reload script
          const webSocketUrls = 'wss://localhost:36013,ws://localhost:37715'.split(',');
          const sharedSecret = await getSecret('MIIBIjANBgkq');
          let connection;
          for (const url of webSocketUrls) {
        """;

    [Theory]
    [InlineData("<html><head><title>x</title></head>", true, "<html><head>")]
    [InlineData("<!doctype html><HTML lang=en><HEAD data-x=\"1\"><title>", false, "<!doctype html><HTML lang=en><HEAD data-x=\"1\">")]
    [InlineData("<html lang=en><body>x</body></html>", false, "<html lang=en>")]
    [InlineData("<html><body><header>x</header>", false, "<html>")]
    [InlineData("<html>", true, "<html>")]
    public void The_script_goes_just_after_head_or_else_html(string html, bool complete, string before)
        => PreviewGateway.FindInjectionPoint(Encoding.ASCII.GetBytes(html), complete).ShouldBe(before.Length);

    [Theory]
    [InlineData("<html><he")]
    [InlineData("<html><head")]
    [InlineData("<!doctype html><html lang=en>")]
    public void A_page_that_has_not_got_that_far_is_waited_for(string html)
        => PreviewGateway.FindInjectionPoint(Encoding.ASCII.GetBytes(html), complete: false).ShouldBe(PreviewGateway.NotYet);

    [Fact]
    public void A_fragment_is_left_alone()
        => PreviewGateway.FindInjectionPoint("<li>fragment</li>"u8, complete: true).ShouldBe(PreviewGateway.Nowhere);

    [Fact]
    public async Task A_page_streams_through_as_soon_as_its_head_has_arrived()
    {
        var upstream = new Pipe();
        var downstream = new Pipe();
        var copy = PreviewGateway.CopyWithNavScriptAsync(upstream.Reader.AsStream(), downstream.Writer.AsStream(), CancellationToken.None);

        await upstream.Writer.WriteAsync(Encoding.UTF8.GetBytes("<html><head><title>x</title></head><body>first"));
        var first = await ReadUntilAsync(downstream.Reader, "first");
        first.ShouldBe("<html><head><script src=\"/__fleet_browser/nav.js\"></script><title>x</title></head><body>first");

        await upstream.Writer.WriteAsync(Encoding.UTF8.GetBytes(" second</body></html>"));
        await upstream.Writer.CompleteAsync();
        await copy;
        await downstream.Writer.CompleteAsync();
        (first + await ReadToEndAsync(downstream.Reader)).ShouldEndWith("<body>first second</body></html>");
    }

    [Fact]
    public async Task A_page_without_a_head_in_its_first_64_KB_passes_through_untouched()
    {
        var page = "<p>" + new string('x', PreviewGateway.InjectionWindow) + "</p><head>";
        using var upstream = new MemoryStream(Encoding.ASCII.GetBytes(page));
        using var downstream = new MemoryStream();

        await PreviewGateway.CopyWithNavScriptAsync(upstream, downstream, CancellationToken.None);

        Encoding.ASCII.GetString(downstream.ToArray()).ShouldBe(page);
    }

    [Fact]
    public void Dotnet_watchs_refresh_script_is_pointed_at_the_preview()
    {
        var (script, port) = PreviewGateway.RewriteRefreshScript(Sdk10RefreshScript);

        port.ShouldBe(37715);
        script.ShouldContain("const webSocketUrls = [(location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/__fleet_browser/ws/37715'];\n");
        script.ShouldNotContain("localhost:");
    }

    [Fact]
    public void A_refresh_script_that_does_not_match_is_passed_on_unchanged()
    {
        const string other = "const webSocketUrls = getUrls();";

        PreviewGateway.RewriteRefreshScript(other).ShouldBe((other, null));
    }

    [Theory]
    [InlineData("localhost", "http://p1.localhost:41234")]
    [InlineData("127.0.0.1", "http://p1.localhost:41234")]
    [InlineData("[::1]", "http://p1.localhost:41234")]
    [InlineData("fleet.localhost", "http://p1.localhost:41234")]
    [InlineData("desktop", "http://desktop:41234")]
    [InlineData("192.168.1.20", "http://192.168.1.20:41234")]
    [InlineData("fd7a:115c::5", "http://[fd7a:115c::5]:41234")]
    public void A_browser_on_Fleets_machine_gets_a_localhost_name_and_others_the_host_they_used(string browserHost, string origin)
        => PreviewGateway.OriginFor(browserHost, new PreviewListener("p1", 41234, new Uri("http://localhost:5173/"), "0.0.0.0")).ShouldBe(origin);

    [Theory]
    [InlineData("http://p1.localhost:41234", "CrossSite")]
    [InlineData("http://192.168.1.20:41234", "Insecure")]
    [InlineData("http://desktop:41234", "Insecure")]
    [InlineData("http://[fd7a:115c::5]:41234", "Insecure")]
    [InlineData("https://desktop:41234", "AsSent")]
    [InlineData("http://localhost:41234", "AsSent")]
    [InlineData("http://127.0.0.1:41234", "AsSent")]
    public void A_previews_cookies_follow_where_the_canvas_loads_it(string proxyOrigin, string rule)
        => PreviewGateway.CookieRuleFor(proxyOrigin).ToString().ShouldBe(rule);

    [Theory]
    // Aspire's dashboard sign-in cookie, as it sends it over https.
    [InlineData(".Aspire.Dashboard.Auth=abc; expires=Mon, 28 Sep 2026 08:12:26 GMT; path=/; secure; samesite=lax; httponly",
        ".Aspire.Dashboard.Auth=abc; expires=Mon, 28 Sep 2026 08:12:26 GMT; path=/; samesite=lax; httponly")]
    [InlineData("a=1; Secure", "a=1")]
    [InlineData("a=1; SameSite=None; Secure; Path=/", "a=1; samesite=lax; Path=/")]
    [InlineData("a=1; domain=localhost; path=/; secure", "a=1; path=/")]
    [InlineData("secure=yes; path=/", "secure=yes; path=/")]
    [InlineData("a=1; path=/secure; HttpOnly", "a=1; path=/secure; HttpOnly")]
    [InlineData("__Host-a=1; path=/; secure", "__Host-a=1; path=/; secure")]
    [InlineData("__Secure-a=1; secure", "__Secure-a=1; secure")]
    public void From_another_device_a_cookie_loses_Secure(string cookie, string expected)
        => PreviewGateway.RewriteSetCookie(cookie, PreviewGateway.CookieRule.Insecure).ShouldBe(expected);

    [Theory]
    // Aspire's antiforgery cookie: Strict, which a frame from another site never keeps.
    [InlineData(".Aspire.Dashboard.Antiforgery=abc; path=/; samesite=strict; httponly", ".Aspire.Dashboard.Antiforgery=abc; path=/; httponly; SameSite=None; Secure")]
    [InlineData("a=1; path=/", "a=1; path=/; SameSite=None; Secure")]
    [InlineData("a=1; domain=localhost; secure; samesite=lax", "a=1; SameSite=None; Secure")]
    [InlineData("__Host-a=1; path=/; secure; samesite=strict", "__Host-a=1; path=/; SameSite=None; Secure")]
    public void On_Fleets_machine_a_cookie_is_kept_across_sites(string cookie, string expected)
        => PreviewGateway.RewriteSetCookie(cookie, PreviewGateway.CookieRule.CrossSite).ShouldBe(expected);

    [Fact]
    public void Over_https_a_cookie_only_loses_its_domain()
        => PreviewGateway.RewriteSetCookie("a=1; domain=localhost; path=/; secure; samesite=none", PreviewGateway.CookieRule.AsSent)
            .ShouldBe("a=1; path=/; secure; samesite=none");

    [Theory]
    [InlineData("127.0.0.1", "Localhost")]
    [InlineData("localhost", "Localhost")]
    [InlineData("::1", "Localhost")]
    [InlineData("0.0.0.0", "Any")]
    [InlineData("::", "Any")]
    [InlineData("desktop.lan", "Any")]
    [InlineData("192.168.1.20", "Address")]
    public void Previews_bind_where_Fleet_binds(string fleetHost, string kind)
        => PreviewGateway.BindFor(fleetHost).Kind.ToString().ShouldBe(kind);

    [Theory]
    [InlineData("41000-41099", 41000, 41099)]
    [InlineData(" 5000 - 5000 ", 5000, 5000)]
    public void A_port_range_is_read_from_first_to_last(string range, int first, int last)
        => PreviewGateway.ParsePortRange(range).ShouldBe((first, last));

    [Theory]
    [InlineData("")]
    [InlineData("41099-41000")]
    [InlineData("41000")]
    [InlineData("0-10")]
    public void A_missing_or_malformed_port_range_means_any_free_port(string range)
        => PreviewGateway.ParsePortRange(range).ShouldBeNull();

    [Fact]
    public async Task A_proxied_page_can_be_framed_takes_the_script_and_keeps_its_websockets()
    {
        await using var upstream = await StartUpstreamAsync();
        await using var gateway = new PreviewGateway(new FleetOptions());

        var preview = await gateway.EnsureAsync(new Uri(upstream.Urls.Single()));
        (await gateway.EnsureAsync(new Uri(upstream.Urls.Single() + "/other"))).ShouldBe(preview);
        preview.Address.ShouldBe("localhost");

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var origin = $"http://127.0.0.1:{preview.Port}";

        using var page = await client.GetAsync(origin + "/");
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Headers.Contains("X-Frame-Options").ShouldBeFalse();
        page.Headers.GetValues("Content-Security-Policy").ShouldBe(["default-src 'self'"]);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<head><script src=\"/__fleet_browser/nav.js\"></script>");

        using var redirect = await client.GetAsync(origin + "/go");
        redirect.Headers.Location!.ToString().ShouldBe(origin + "/landed");

        (await client.GetStringAsync(origin + PreviewGateway.ScriptPath)).ShouldBe(PreviewGateway.BridgeScript.Value);
        PreviewGateway.BridgeScript.Value.ShouldContain("var VERSION = 1;");

        using var framed = new HttpRequestMessage(HttpMethod.Get, origin + "/dest");
        framed.Headers.TryAddWithoutValidation("sec-fetch-dest", "iframe");
        (await (await client.SendAsync(framed)).Content.ReadAsStringAsync()).ShouldBe("document");

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("vite-hmr");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{preview.Port}/hmr"), CancellationToken.None);
        socket.SubProtocol.ShouldBe("vite-hmr");
        await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("echo:ping");

        // The app closes right after its reply; the close reaches the browser as a close, not a reset.
        var closing = await socket.ReceiveAsync(buffer, CancellationToken.None);
        closing.MessageType.ShouldBe(WebSocketMessageType.Close);
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_streamed_page_shows_its_first_part_before_the_rest_is_ready()
    {
        var rest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await StartUpstreamAsync(rest.Task);
        await using var gateway = new PreviewGateway(new FleetOptions());
        var preview = await gateway.EnsureAsync(new Uri(upstream.Urls.Single()));

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{preview.Port}/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();

        var first = await ReadUntilAsync(PipeReader.Create(body), "shell").WaitAsync(TimeSpan.FromSeconds(5));
        first.ShouldContain("<head><script src=\"/__fleet_browser/nav.js\"></script><title>Streaming</title>");
        rest.SetResult();
    }

    [Fact]
    public async Task Dotnet_watchs_refresh_socket_comes_through_the_preview_with_the_apps_origin()
    {
        string? appOrigin = null;
        await using var refreshServer = await StartRefreshServerAsync(() => appOrigin);
        var refreshPort = new Uri(refreshServer.Urls.Single()).Port;
        await using var upstream = await StartUpstreamAsync(refreshPort: refreshPort);
        appOrigin = new Uri(upstream.Urls.Single()).GetLeftPart(UriPartial.Authority);
        await using var gateway = new PreviewGateway(new FleetOptions());
        var preview = await gateway.EnsureAsync(new Uri(upstream.Urls.Single()));
        var origin = $"127.0.0.1:{preview.Port}";

        // Before the page's script named it, the refresh server can't be reached through the preview.
        using (var early = new ClientWebSocket())
            await Should.ThrowAsync<WebSocketException>(() => early.ConnectAsync(new Uri($"ws://{origin}/__fleet_browser/ws/{refreshPort}"), CancellationToken.None));

        using var client = new HttpClient();
        var script = await client.GetStringAsync($"http://{origin}{PreviewGateway.RefreshScriptPath}");
        script.ShouldContain($"'/__fleet_browser/ws/{refreshPort}'");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{origin}/__fleet_browser/ws/{refreshPort}"), CancellationToken.None);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("Reload");

        using var other = new ClientWebSocket();
        await Should.ThrowAsync<WebSocketException>(() => other.ConnectAsync(new Uri($"ws://{origin}/__fleet_browser/ws/{refreshPort + 1}"), CancellationToken.None));
    }

    [Fact]
    public async Task A_preview_of_an_app_that_is_not_there_says_so()
    {
        var deadPort = FreeLoopbackPort();
        await using var gateway = new PreviewGateway(new FleetOptions());
        var preview = await gateway.EnsureAsync(new Uri($"http://localhost:{deadPort}/"));

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{preview.Port}/");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).ShouldContain($"Nothing answers at <code>http://localhost:{deadPort}</code>");
    }

    [Fact]
    public async Task A_Fleet_on_localhost_keeps_its_previews_off_the_network_and_an_open_Fleet_opens_them()
    {
        if (NonLoopbackAddress() is not { } lan)
            return;

        await using var upstream = await StartUpstreamAsync();
        await using var local = new PreviewGateway(new FleetOptions { Host = "127.0.0.1" });
        await using var open = new PreviewGateway(new FleetOptions { Host = "0.0.0.0" });
        var localPreview = await local.EnsureAsync(new Uri(upstream.Urls.Single()));
        var openPreview = await open.EnsureAsync(new Uri(upstream.Urls.Single()));

        (await CanConnectAsync(lan, localPreview.Port)).ShouldBeFalse();
        (await CanConnectAsync(lan, openPreview.Port)).ShouldBeTrue();
        openPreview.Address.ShouldBe("0.0.0.0");
    }

    [Fact]
    public async Task Previews_take_their_ports_from_the_configured_range()
    {
        var port = FreeLoopbackPort();
        await using var upstream = await StartUpstreamAsync();
        await using var gateway = new PreviewGateway(new FleetOptions { Browser = new BrowserOptions { PortRange = $"{port}-{port}" } });

        (await gateway.EnsureAsync(new Uri(upstream.Urls.Single()))).Port.ShouldBe(port);
        await Should.ThrowAsync<IOException>(() => gateway.EnsureAsync(new Uri("http://localhost:1/")));
    }

    /// <summary>
    /// A dev server that forbids framing, redirects to itself, echoes on a websocket, reports the
    /// <c>Sec-Fetch-Dest</c> it got, streams a page in two parts, and serves a refresh script naming <paramref name="refreshPort"/>.
    /// </summary>
    private static async Task<WebApplication> StartUpstreamAsync(Task? streamRest = null, int refreshPort = 0)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync(context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault());
                var buffer = new byte[64];
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                await ws.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(buffer, 0, result.Count)), WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            switch (context.Request.Path.Value)
            {
                case "/go":
                    context.Response.Redirect($"http://{context.Request.Host}/landed");
                    return;
                case "/dest":
                    await context.Response.WriteAsync(context.Request.Headers["Sec-Fetch-Dest"].ToString());
                    return;
                case "/stream":
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync("<!doctype html><html><head><title>Streaming</title></head><body>shell");
                    await context.Response.Body.FlushAsync();
                    await (streamRest ?? Task.CompletedTask);
                    await context.Response.WriteAsync(" rest</body></html>");
                    return;
                case PreviewGateway.RefreshScriptPath:
                    context.Response.ContentType = "application/javascript";
                    await context.Response.WriteAsync(Sdk10RefreshScript.Replace("37715", refreshPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
                    return;
            }

            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<!doctype html><html><head><title>Upstream</title></head><body>hi</body></html>");
        });
        await app.StartAsync();
        return app;
    }

    /// <summary>Like <c>dotnet watch</c>'s refresh server: a websocket that refuses any Origin but the app's, then says "Reload".</summary>
    private static async Task<WebApplication> StartRefreshServerAsync(Func<string?> appOrigin)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.ToString() != appOrigin())
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await ws.SendAsync("Reload"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<string> ReadUntilAsync(PipeReader reader, string marker)
    {
        var text = new StringBuilder();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
                text.Append(Encoding.UTF8.GetString(segment.Span));
            reader.AdvanceTo(result.Buffer.End);
            if (text.ToString().Contains(marker, StringComparison.Ordinal) || result.IsCompleted)
                return text.ToString();
        }
    }

    private static async Task<string> ReadToEndAsync(PipeReader reader)
    {
        var text = new StringBuilder();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
                text.Append(Encoding.UTF8.GetString(segment.Span));
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                return text.ToString();
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress? NonLoopbackAddress()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private static async Task<bool> CanConnectAsync(IPAddress address, int port)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }
}
