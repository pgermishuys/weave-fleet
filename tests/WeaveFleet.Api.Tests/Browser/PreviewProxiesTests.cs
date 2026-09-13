using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Api.Browser;

namespace WeaveFleet.Api.Tests.Browser;

public sealed class PreviewProxiesTests
{
    [Theory]
    [InlineData("<html><head><title>x</title></head></html>", "<html><head><script src=\"/__fleet_browser/nav.js\"></script><title>x</title></head></html>")]
    [InlineData("<HTML lang=en><body>x</body></HTML>", "<HTML lang=en><script src=\"/__fleet_browser/nav.js\"></script><body>x</body></HTML>")]
    [InlineData("<li>fragment</li>", "<li>fragment</li>")]
    public void The_navigation_script_goes_first_in_the_head_and_fragments_are_left_alone(string html, string expected)
        => PreviewProxies.InjectNavScript(html).ShouldBe(expected);

    [Theory]
    [InlineData("http://localhost:5199/sessions?x=1", "http://p1.localhost:4000/sessions?x=1")]
    [InlineData("http://127.0.0.1:5199/", "http://p1.localhost:4000/")]
    [InlineData("/relative", "/relative")]
    [InlineData("https://localhost:7001/", "https://localhost:7001/")]
    [InlineData("https://github.com/login", "https://github.com/login")]
    public void Redirects_to_the_app_stay_in_the_proxy(string location, string expected)
        => PreviewProxies.RewriteLocation(location, "http://p1.localhost:4000", new Uri("http://localhost:5199/")).ShouldBe(expected);

    [Fact]
    public void Only_frame_ancestors_is_removed_from_a_policy()
        => PreviewProxies.WithoutFrameAncestors("default-src 'self'; frame-ancestors 'self';script-src 'self'")
            .ShouldBe("default-src 'self'; script-src 'self'");

    [Fact]
    public async Task A_proxied_page_can_be_framed_takes_the_script_and_keeps_its_websockets()
    {
        await using var upstream = await StartUpstreamAsync();
        await using var proxies = new PreviewProxies();

        var proxy = await proxies.EnsureAsync(new Uri(upstream.Urls.Single()));
        (await proxies.EnsureAsync(new Uri(upstream.Urls.Single() + "/other"))).ShouldBe(proxy);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var origin = $"http://127.0.0.1:{proxy.Port}";

        using var page = await client.GetAsync(origin + "/");
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Headers.Contains("X-Frame-Options").ShouldBeFalse();
        page.Headers.GetValues("Content-Security-Policy").ShouldBe(["default-src 'self'"]);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<head><script src=\"/__fleet_browser/nav.js\"></script>");

        using var redirect = await client.GetAsync(origin + "/go");
        redirect.Headers.Location!.ToString().ShouldBe(origin + "/landed");

        (await client.GetStringAsync(origin + PreviewProxies.ScriptPath)).ShouldContain("fleet-browser:location");

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("vite-hmr");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{proxy.Port}/hmr"), CancellationToken.None);
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

    /// <summary>A dev server that forbids framing, redirects to itself, and echoes on a websocket.</summary>
    private static async Task<WebApplication> StartUpstreamAsync()
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

            if (context.Request.Path == "/go")
            {
                context.Response.Redirect($"http://{context.Request.Host}/landed");
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
}
