using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebSockets;

namespace WeaveFleet.Api.Browser;

/// <summary>A running proxy: the browser loads <c>http://{Slug}.localhost:{Port}</c> and gets <see cref="Target"/>.</summary>
public sealed record PreviewProxy(string Slug, int Port, Uri Target);

/// <summary>
/// One small loopback server per app origin, in front of a dev server, so a browser canvas can frame it.
/// Each proxy is its own origin, so the app's absolute paths, cookies and websockets (hot reload) work
/// unchanged and it can't read Fleet's cookies. The proxy removes the headers that forbid framing, keeps
/// redirects inside the proxy, and adds a small script that reports navigation to the canvas.
/// Local only for now: the proxy listens on loopback.
/// </summary>
public sealed partial class PreviewProxies : IAsyncDisposable
{
    public const string ScriptPath = "/__fleet_browser/nav.js";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Trailer",
        "Proxy-Authenticate", "Proxy-Authorization",
    };

    // Reports the page to the canvas, and takes back/forward/reload from it. Cross-origin, so postMessage only.
    private const string NavScript = """
        (function () {
          if (window.top === window) return;
          function report() {
            parent.postMessage({ type: "fleet-browser:location", href: location.href, title: document.title }, "*");
          }
          ["pushState", "replaceState"].forEach(function (name) {
            var original = history[name];
            history[name] = function () { var result = original.apply(this, arguments); report(); return result; };
          });
          addEventListener("popstate", report);
          addEventListener("hashchange", report);
          addEventListener("load", report);
          addEventListener("message", function (event) {
            var data = event.data;
            if (!data || data.type !== "fleet-browser:nav") return;
            if (data.action === "back") history.back();
            else if (data.action === "forward") history.forward();
            else if (data.action === "reload") location.reload();
          });
          report();
        })();
        """;

    private readonly ConcurrentDictionary<string, Lazy<Task<Running>>> _byOrigin = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpMessageInvoker _client;

    public PreviewProxies()
    {
        // The upstream is a dev server on this machine, usually with a self-signed development certificate.
#pragma warning disable CA5359
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
#pragma warning restore CA5359
    }

    /// <summary>The proxy for <paramref name="target"/>'s origin, started on first use.</summary>
    public async Task<PreviewProxy> EnsureAsync(Uri target)
    {
        var origin = new Uri(target.GetLeftPart(UriPartial.Authority));
        var lazy = _byOrigin.GetOrAdd(origin.ToString(), _ => new Lazy<Task<Running>>(() => StartAsync(origin)));
        try
        {
            return (await lazy.Value).Proxy;
        }
        catch
        {
            _byOrigin.TryRemove(origin.ToString(), out _);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _byOrigin.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.App.DisposeAsync();
        }

        _client.Dispose();
    }

    private async Task<Running> StartAsync(Uri target)
    {
        var port = FreePort();
        var proxy = new PreviewProxy("p" + Ulid.NewUlid().ToString()[^8..].ToLowerInvariant(), port, target);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
        builder.Services.AddWebSockets(_ => { });

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(context => HandleAsync(context, target));
        await app.StartAsync();
        return new Running(proxy, app);
    }

    private async Task HandleAsync(HttpContext context, Uri target)
    {
        if (context.Request.Path.Equals(ScriptPath, StringComparison.Ordinal))
        {
            context.Response.ContentType = "text/javascript; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(NavScript, context.RequestAborted);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await ForwardWebSocketAsync(context, target);
            return;
        }

        await ForwardHttpAsync(context, target);
    }

    private async Task ForwardHttpAsync(HttpContext context, Uri target)
    {
        var proxyOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var targetOrigin = target.GetLeftPart(UriPartial.Authority);
        var upstreamUri = new Uri(target, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent());

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamUri);
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            request.Content = new StreamContent(context.Request.Body);

        foreach (var (name, values) in context.Request.Headers)
        {
            // Ask for an uncompressed body, so HTML can take the navigation script.
            if (HopByHopHeaders.Contains(name) || name.StartsWith(':') || name is "Host" or "Accept-Encoding")
                continue;

            var rewritten = name is "Origin" or "Referer"
                ? values.Select(value => ReplaceOrigin(value ?? string.Empty, proxyOrigin, targetOrigin)).ToArray()
                : values.ToArray();
            if (!request.Headers.TryAddWithoutValidation(name, rewritten))
                request.Content?.Headers.TryAddWithoutValidation(name, rewritten);
        }

        request.Headers.Host = target.Authority;

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, context.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"Fleet couldn't reach {targetOrigin}: {ex.Message}", context.RequestAborted);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response.Headers, context.Response, proxyOrigin, target);
            CopyResponseHeaders(response.Content.Headers, context.Response, proxyOrigin, target);

            var isHtml = response.Content.Headers.ContentType?.MediaType == "text/html"
                         && response.Content.Headers.ContentEncoding.Count == 0
                         && HttpMethods.IsGet(context.Request.Method);
            if (isHtml)
            {
                var html = await response.Content.ReadAsStringAsync(context.RequestAborted);
                var injected = InjectNavScript(html);
                context.Response.Headers.ContentLength = null;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(injected, Encoding.UTF8, context.RequestAborted);
                return;
            }

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static async Task ForwardWebSocketAsync(HttpContext context, Uri target)
    {
        var upstreamUri = new UriBuilder(target)
        {
            Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = context.Request.Path.ToUriComponent(),
            Query = context.Request.QueryString.ToUriComponent().TrimStart('?'),
        }.Uri;

        using var upstream = new ClientWebSocket();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);
        upstream.Options.SetRequestHeader("Origin", target.GetLeftPart(UriPartial.Authority));
        if (context.Request.Headers.Cookie.Count > 0)
            upstream.Options.SetRequestHeader("Cookie", context.Request.Headers.Cookie.ToString());
#pragma warning disable CA5359 // A dev server on this machine; see the HTTP client above.
        upstream.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359

        try
        {
            await upstream.ConnectAsync(upstreamUri, context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var first = await Task.WhenAny(PumpAsync(downstream, upstream, cts.Token), PumpAsync(upstream, downstream, cts.Token));
        await cts.CancelAsync();
        await first;
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await to.CloseOutputAsync(from.CloseStatus ?? WebSocketCloseStatus.NormalClosure, from.CloseStatusDescription, ct);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // One side went away; the other is closed when the request ends.
        }
    }

    private static void CopyResponseHeaders(System.Net.Http.Headers.HttpHeaders headers, HttpResponse response, string proxyOrigin, Uri target)
    {
        foreach (var (name, values) in headers)
        {
            if (HopByHopHeaders.Contains(name) || name is "X-Frame-Options" or "Strict-Transport-Security")
                continue;

            var copied = name switch
            {
                "Content-Security-Policy" => values.Select(WithoutFrameAncestors).Where(value => value.Length > 0).ToArray(),
                "Location" => values.Select(value => RewriteLocation(value, proxyOrigin, target)).ToArray(),
                "Set-Cookie" => values.Select(value => CookieDomain().Replace(value, string.Empty)).ToArray(),
                _ => values.ToArray(),
            };
            if (copied.Length > 0)
                response.Headers[name] = copied;
        }
    }

    /// <summary>Puts the navigation script first in <c>&lt;head&gt;</c>. Fragments without a head or html tag are left alone.</summary>
    internal static string InjectNavScript(string html)
    {
        const string tag = "<script src=\"" + ScriptPath + "\"></script>";
        var match = HeadOpen().Match(html);
        if (!match.Success)
            match = HtmlOpen().Match(html);
        return match.Success ? html.Insert(match.Index + match.Length, tag) : html;
    }

    /// <summary>A redirect to the app itself stays in the proxy. Redirects elsewhere are left as they are.</summary>
    internal static string RewriteLocation(string location, string proxyOrigin, Uri target)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Port != target.Port)
            return location;
        if (!WeaveFleet.Application.Canvases.LoopbackUrl.IsLoopbackHost(uri.Host))
            return location;

        return proxyOrigin + uri.PathAndQuery + uri.Fragment;
    }

    internal static string WithoutFrameAncestors(string policy)
        => string.Join("; ", policy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(directive => !directive.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase)));

    private static string ReplaceOrigin(string value, string from, string to)
        => value.StartsWith(from, StringComparison.OrdinalIgnoreCase) ? to + value[from.Length..] : value;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<html(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();

    [GeneratedRegex(@";\s*domain=[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex CookieDomain();

    private sealed record Running(PreviewProxy Proxy, WebApplication App);
}
