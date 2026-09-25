using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebSockets;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Browser;

/// <summary>
/// A running preview listener in front of <see cref="Target"/>'s origin. <see cref="Address"/> is where it
/// listens (<c>localhost</c>, <c>0.0.0.0</c>, or an address of this machine).
/// </summary>
public sealed record PreviewListener(string Slug, int Port, Uri Target, string Address);

/// <summary>
/// Gives each dev server a browser canvas can frame its own origin: one small listener per app origin, so the
/// app's absolute paths, cookies and websockets (hot reload) work unchanged. It removes the headers that forbid
/// framing, keeps redirects inside the preview, streams pages while adding a small script that reports
/// navigation to the canvas, and routes <c>dotnet watch</c>'s browser-refresh socket through the preview.
/// <para>
/// Listeners bind where Fleet binds (<see cref="FleetOptions.Host"/>): a Fleet on localhost keeps its previews
/// on localhost; a Fleet open to the network opens its previews to the same network, without Fleet's login
/// (plan Decision 12). The browser reaches a preview at <c>{slug}.localhost:{port}</c> on Fleet's machine, or at
/// the host it used to reach Fleet otherwise (<see cref="OriginFor"/>).
/// </para>
/// </summary>
public sealed partial class PreviewGateway : IAsyncDisposable
{
    public const string ScriptPath = "/__fleet_browser/nav.js";

    /// <summary>Where the rewritten <c>dotnet watch</c> refresh script connects: <c>{RefreshSocketPath}/{port}</c> on the preview.</summary>
    public const string RefreshSocketPath = "/__fleet_browser/ws";

    /// <summary>The script <c>dotnet watch</c> injects into pages; it names the refresh servers it connects to.</summary>
    public const string RefreshScriptPath = "/_framework/aspnetcore-browser-refresh.js";

    /// <summary>How far into a page Fleet looks for <c>&lt;head&gt;</c> before passing the page on untouched.</summary>
    internal const int InjectionWindow = 64 * 1024;

    /// <summary>How long a websocket's other side gets to answer a close before the gateway drops both.</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private static readonly byte[] ScriptTag = Encoding.ASCII.GetBytes("<script src=\"" + ScriptPath + "\"></script>");

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Trailer",
        "Proxy-Authenticate", "Proxy-Authorization",
    };

    /// <summary>
    /// The preview bridge (protocol v1, <c>Browser/preview-bridge.js</c>): reports the page, its hot-reload client
    /// and its hot updates to the canvas, and takes back/forward/reload from it.
    /// </summary>
    internal static readonly Lazy<string> BridgeScript = new(() =>
    {
        using var stream = typeof(PreviewGateway).Assembly.GetManifestResourceStream("browser/preview-bridge.js")
            ?? throw new InvalidOperationException("Embedded resource browser/preview-bridge.js is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    private readonly ConcurrentDictionary<string, Lazy<Task<Running>>> _byOrigin = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpMessageInvoker _client;
    private readonly string _bindHost;
    private readonly (int First, int Last)? _portRange;
    private readonly Func<int, bool> _isAppPort;

    public PreviewGateway(FleetOptions options, IAppRunner apps)
        : this(options, apps.IsAppPort)
    {
    }

    /// <param name="isAppPort">Whether an app Fleet runs listens on a port: redirects there get a preview.</param>
    internal PreviewGateway(FleetOptions options, Func<int, bool>? isAppPort = null)
    {
        _isAppPort = isAppPort ?? (_ => false);
        _bindHost = options.Host;
        _portRange = ParsePortRange(options.Browser.PortRange);

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

    /// <summary>The listener for <paramref name="target"/>'s origin, started on first use.</summary>
    public async Task<PreviewListener> EnsureAsync(Uri target)
    {
        var origin = new Uri(target.GetLeftPart(UriPartial.Authority));
        var lazy = _byOrigin.GetOrAdd(origin.ToString(), _ => new Lazy<Task<Running>>(() => StartAsync(origin)));
        try
        {
            return (await lazy.Value).Listener;
        }
        catch
        {
            _byOrigin.TryRemove(origin.ToString(), out _);
            throw;
        }
    }

    /// <summary>
    /// Where a browser that reached Fleet at <paramref name="browserHost"/> loads the preview. On Fleet's machine
    /// each preview gets its own <c>*.localhost</c> name, which keeps its cookies apart; from another device, the
    /// same host with the preview's port.
    /// </summary>
    public static string OriginFor(string browserHost, PreviewListener listener)
    {
        var host = browserHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
        {
            return $"http://{listener.Slug}.localhost:{listener.Port}";
        }

        var authority = IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
        return $"http://{authority}:{listener.Port}";
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
        var slug = "p" + Ulid.NewUlid().ToString()[^8..].ToLowerInvariant();
        var (bind, address) = BindFor(_bindHost);

        // A port can be taken between choosing it and binding it; try a few.
        for (var attempt = 0; ; attempt++)
        {
            var port = ChoosePort(address);
            var running = new Running(new PreviewListener(slug, port, target, bind == BindKind.Localhost ? "localhost" : address.ToString()));

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                switch (bind)
                {
                    case BindKind.Localhost:
                        kestrel.ListenLocalhost(port);
                        break;
                    case BindKind.Any:
                        kestrel.ListenAnyIP(port);
                        break;
                    default:
                        kestrel.Listen(address, port);
                        break;
                }
            });
            builder.Services.AddWebSockets(_ => { });

            var app = builder.Build();
            app.UseWebSockets();
            app.Run(context => HandleAsync(context, running));
            try
            {
                await app.StartAsync();
                running.App = app;
                return running;
            }
            catch (IOException) when (attempt < 4)
            {
                await app.DisposeAsync();
            }
        }
    }

    private async Task HandleAsync(HttpContext context, Running running)
    {
        var target = running.Listener.Target;
        if (context.Request.Path.Equals(ScriptPath, StringComparison.Ordinal))
        {
            context.Response.ContentType = "text/javascript; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(BridgeScript.Value, context.RequestAborted);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            if (context.Request.Path.StartsWithSegments(RefreshSocketPath, out var rest))
            {
                // Only the refresh servers named in a refresh script this preview served: the route is not a
                // way to reach any socket on this machine.
                if (int.TryParse(rest.Value?.Trim('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var refreshPort)
                    && running.RefreshPorts.ContainsKey(refreshPort))
                {
                    await ForwardWebSocketAsync(context, target, new Uri($"ws://localhost:{refreshPort}/"));
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }
                return;
            }

            await ForwardWebSocketAsync(context, target, upstreamUri: null);
            return;
        }

        await ForwardHttpAsync(context, running);
    }

    private async Task ForwardHttpAsync(HttpContext context, Running running)
    {
        var target = running.Listener.Target;
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

            string?[] rewritten = name switch
            {
                "Origin" or "Referer" => [.. values.Select(value => ReplaceOrigin(value ?? string.Empty, proxyOrigin, targetOrigin))],
                // The canvas frames every page, but the app should serve it as it would a top-level page:
                // ASP.NET's browser-refresh middleware, for one, adds nothing to framed pages.
                _ when name.Equals("Sec-Fetch-Dest", StringComparison.OrdinalIgnoreCase) && values.ToString() == "iframe" => ["document"],
                _ => values.ToArray(),
            };
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
            await WriteUnreachableAsync(context, targetOrigin, ex.Message);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var location = response.Headers.Location is { } redirect
                ? await PreviewLocationAsync(redirect.OriginalString, context.Request.Host.Host, proxyOrigin, target)
                : null;
            CopyResponseHeaders(response.Headers, context.Response, proxyOrigin, target, location);
            CopyResponseHeaders(response.Content.Headers, context.Response, proxyOrigin, target, location);
            var uncompressed = response.Content.Headers.ContentEncoding.Count == 0;

            if (uncompressed && context.Request.Path.Equals(RefreshScriptPath, StringComparison.Ordinal))
            {
                var script = await response.Content.ReadAsStringAsync(context.RequestAborted);
                var (rewritten, refreshPort) = RewriteRefreshScript(script);
                if (refreshPort is { } port)
                    running.RefreshPorts[port] = 0;
                context.Response.Headers.ContentLength = null;
                await context.Response.WriteAsync(rewritten, Encoding.UTF8, context.RequestAborted);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var isHtml = uncompressed
                         && response.Content.Headers.ContentType?.MediaType == "text/html"
                         && HttpMethods.IsGet(context.Request.Method);
            if (isHtml)
            {
                context.Response.Headers.ContentLength = null;
                await CopyWithNavScriptAsync(body, context.Response.Body, context.RequestAborted);
                return;
            }

            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// Copies a page, adding the navigation script right after <c>&lt;head…&gt;</c> (or <c>&lt;html…&gt;</c> when
    /// the page has no head). Only the bytes up to that point are held, at most <see cref="InjectionWindow"/>; the
    /// rest streams through as it arrives, so a page rendered in pieces shows its first piece at once.
    /// </summary>
    internal static async Task CopyWithNavScriptAsync(Stream upstream, Stream downstream, CancellationToken ct)
    {
        var buffer = new byte[InjectionWindow];
        var filled = 0;
        var complete = false;
        while (true)
        {
            var at = FindInjectionPoint(buffer.AsSpan(0, filled), complete || filled == buffer.Length);
            if (at != NotYet)
            {
                if (at >= 0)
                {
                    await downstream.WriteAsync(buffer.AsMemory(0, at), ct);
                    await downstream.WriteAsync(ScriptTag, ct);
                    await downstream.WriteAsync(buffer.AsMemory(at, filled - at), ct);
                }
                else
                {
                    await downstream.WriteAsync(buffer.AsMemory(0, filled), ct);
                }
                break;
            }

            var read = await upstream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                complete = true;
            filled += read;
        }

        await downstream.FlushAsync(ct);
        await upstream.CopyToAsync(downstream, ct);
    }

    /// <summary><see cref="FindInjectionPoint"/>: the page so far doesn't say yet.</summary>
    internal const int NotYet = -1;

    /// <summary><see cref="FindInjectionPoint"/>: the page has nowhere to put the script (a fragment, say).</summary>
    internal const int Nowhere = -2;

    /// <summary>
    /// Where the navigation script goes in the start of a page: just after <c>&lt;head…&gt;</c>; or, once the page
    /// reaches <c>&lt;body</c> or ends without a head, just after <c>&lt;html…&gt;</c>. Works on bytes, so the
    /// page's encoding doesn't matter as long as it's ASCII-compatible.
    /// </summary>
    internal static int FindInjectionPoint(ReadOnlySpan<byte> html, bool complete)
    {
        var head = EndOfOpeningTag(html, "head"u8);
        if (head is >= 0 or NotYet)
            return head;

        if (!complete && TagStart(html, "body"u8) < 0)
            return NotYet;

        var root = EndOfOpeningTag(html, "html"u8);
        return root >= 0 ? root : Nowhere;
    }

    /// <summary>The index just past <c>&lt;{name}…&gt;</c>; <see cref="NotYet"/> if the tag's '&gt;' hasn't arrived; <see cref="Nowhere"/> if there's no such tag.</summary>
    private static int EndOfOpeningTag(ReadOnlySpan<byte> html, ReadOnlySpan<byte> name)
    {
        var start = TagStart(html, name);
        if (start < 0)
            return Nowhere;

        var close = html[start..].IndexOf((byte)'>');
        return close < 0 ? NotYet : start + close + 1;
    }

    /// <summary>Where <c>&lt;{name}</c> starts, followed by whitespace, '&gt;' or '/', case-insensitive; -1 if it doesn't.</summary>
    private static int TagStart(ReadOnlySpan<byte> html, ReadOnlySpan<byte> name)
    {
        for (var i = html.IndexOf((byte)'<'); i >= 0 && i + name.Length + 1 < html.Length; )
        {
            var candidate = html.Slice(i + 1, name.Length);
            var after = html[i + 1 + name.Length];
            if (Ascii.EqualsIgnoreCase(candidate, name) && (after is (byte)'>' or (byte)'/' || char.IsWhiteSpace((char)after)))
                return i;

            var next = html[(i + 1)..].IndexOf((byte)'<');
            i = next < 0 ? -1 : i + 1 + next;
        }
        return -1;
    }

    /// <summary>
    /// <c>dotnet watch</c>'s refresh script names its servers as a literal,
    /// <c>const webSocketUrls = 'wss://localhost:A,ws://localhost:B'.split(',');</c> (SDK 10.0.112). The <c>ws://</c>
    /// one becomes <c>{RefreshSocketPath}/B</c> on the preview's own origin, so the socket comes through the
    /// gateway, which sends the app's <c>Origin</c> (the refresh server refuses any other). A script that doesn't
    /// match is passed on unchanged, and the page falls back to reloading when the app answers again.
    /// </summary>
    internal static (string Script, int? RefreshPort) RewriteRefreshScript(string script)
    {
        int? port = null;
        var rewritten = RefreshUrls().Replace(script, match =>
        {
            var ws = match.Groups["urls"].Value.Split(',').FirstOrDefault(url => url.StartsWith("ws://", StringComparison.Ordinal));
            if (ws is null || !Uri.TryCreate(ws, UriKind.Absolute, out var uri) || !LoopbackUrl.IsLoopbackHost(uri.Host))
                return match.Value;

            port = uri.Port;
            return "[(location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '" + RefreshSocketPath + "/"
                + uri.Port.ToString(CultureInfo.InvariantCulture) + "'];";
        }, count: 1);
        return (rewritten, port);
    }

    private static async Task WriteUnreachableAsync(HttpContext context, string targetOrigin, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.Headers.CacheControl = "no-store";
        var wantsPage = context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (!wantsPage)
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"Fleet couldn't reach {targetOrigin}: {reason}", context.RequestAborted);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(UnreachablePage(targetOrigin, reason), context.RequestAborted);
    }

    /// <summary>What a preview shows when nothing answers at its address: the app stopped, or hasn't started listening.</summary>
    internal static string UnreachablePage(string targetOrigin, string reason)
    {
        var where = WebUtility.HtmlEncode(targetOrigin);
        var why = WebUtility.HtmlEncode(reason);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Nothing at {{where}}</title>
            <meta http-equiv="refresh" content="3">
            <style>
              :root { color-scheme: light dark; --bg: #faf9f7; --fg: #2b2926; --muted: #7a746c; }
              @media (prefers-color-scheme: dark) { :root { --bg: #1c1b19; --fg: #e8e5e0; --muted: #9c968d; } }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg);
                     font: 14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
              main { max-width: 32rem; padding: 0 1.5rem; }
              h1 { font-size: 15px; font-weight: 600; margin: 0 0 .4rem; }
              p { margin: 0 0 .4rem; color: var(--muted); }
              code { font: 12.5px ui-monospace, SFMono-Regular, Menlo, monospace; }
            </style></head>
            <body><main>
              <h1>Nothing answers at <code>{{where}}</code></h1>
              <p>The app may have stopped, or not be listening yet. This page tries again every few seconds.</p>
              <p><code>{{why}}</code></p>
            </main></body></html>
            """;
    }

    private static async Task ForwardWebSocketAsync(HttpContext context, Uri target, Uri? upstreamUri)
    {
        upstreamUri ??= new UriBuilder(target)
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
        var toUpstream = PumpAsync(downstream, upstream, cts.Token);
        var toDownstream = PumpAsync(upstream, downstream, cts.Token);
        await Task.WhenAny(toUpstream, toDownstream);

        // One side closed and the close was passed on. Give the other side a moment to answer it: cancelling
        // a pending receive aborts that socket, which can reset the connection before the last messages and
        // the close handshake get through.
        try
        {
            await Task.WhenAll(toUpstream, toDownstream).WaitAsync(CloseGrace);
        }
        catch (TimeoutException)
        {
            // The other side never answered the close; tear both down.
        }

        await cts.CancelAsync();
        await Task.WhenAll(toUpstream, toDownstream);
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

    private static void CopyResponseHeaders(System.Net.Http.Headers.HttpHeaders headers, HttpResponse response, string proxyOrigin, Uri target, string? location)
    {
        foreach (var (name, values) in headers)
        {
            if (HopByHopHeaders.Contains(name) || name is "X-Frame-Options" or "Strict-Transport-Security")
                continue;

            var copied = name switch
            {
                "Content-Security-Policy" => values.Select(WithoutFrameAncestors).Where(value => value.Length > 0).ToArray(),
                "Location" => location is null ? values.Select(value => RewriteLocation(value, proxyOrigin, target)).ToArray() : [location],
                "Set-Cookie" => values.Select(value => RewriteSetCookie(value, CookieRuleFor(proxyOrigin))).ToArray(),
                _ => values.ToArray(),
            };
            if (copied.Length > 0)
                response.Headers[name] = copied;
        }
    }

    /// <summary>What a preview's cookies need so the browser keeps them in the canvas (see <see cref="CookieRuleFor"/>).</summary>
    internal enum CookieRule
    {
        /// <summary>As the app sent them.</summary>
        AsSent,

        /// <summary><c>SameSite=None; Secure</c>: the canvas frames the preview from another site.</summary>
        CrossSite,

        /// <summary>Without <c>Secure</c>: the browser doesn't count the preview as secure.</summary>
        Insecure,
    }

    /// <summary>
    /// On Fleet's machine a preview is a <c>*.localhost</c> name (<see cref="OriginFor"/>), another site than the
    /// Fleet page that frames it, and a browser keeps no <c>Lax</c> or <c>Strict</c> cookie in a frame from
    /// another site: that's the default, so no app could sign in. Those names count as secure, so the cookies
    /// can be <c>SameSite=None; Secure</c>. From another device a preview is plain http on the host the browser
    /// used for Fleet: the same site, but not secure, so a <c>Secure</c> cookie would be dropped.
    /// </summary>
    internal static CookieRule CookieRuleFor(string proxyOrigin)
    {
        if (!Uri.TryCreate(proxyOrigin, UriKind.Absolute, out var origin))
            return CookieRule.AsSent;
        if (origin.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return CookieRule.CrossSite;
        if (origin.Scheme == Uri.UriSchemeHttps)
            return CookieRule.AsSent;

        var host = origin.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            ? CookieRule.AsSent
            : CookieRule.Insecure;
    }

    /// <summary>
    /// A cookie is for the preview's host, not the app's, and gets what <paramref name="rule"/> says the canvas
    /// needs. Sign-in cookies are why: an https dev server marks them <c>Secure</c> (Aspire's dashboard), and
    /// ASP.NET makes them <c>Lax</c> or <c>Strict</c>; the user would sign in and land on the sign-in page again.
    /// Without <c>Secure</c>, <c>SameSite=None</c> (which needs it) becomes <c>Lax</c>. A <c>__Secure-</c> or
    /// <c>__Host-</c> cookie can't lose <c>Secure</c> at all, so there it's left as the app sent it.
    /// </summary>
    internal static string RewriteSetCookie(string cookie, CookieRule rule)
    {
        cookie = CookieDomain().Replace(cookie, string.Empty);
        switch (rule)
        {
            case CookieRule.CrossSite:
                cookie = CookieSameSite().Replace(CookieSecure().Replace(cookie, string.Empty), string.Empty);
                return cookie + "; SameSite=None; Secure";
            case CookieRule.Insecure when !cookie.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase)
                                          && !cookie.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase):
                cookie = CookieSecure().Replace(cookie, string.Empty);
                return CookieSameSiteNone().Replace(cookie, "; samesite=lax");
            default:
                return cookie;
        }
    }

    /// <summary>
    /// Where a redirect sends the browser. The app's own address stays in this preview (<see cref="RewriteLocation"/>).
    /// Another port of an app Fleet runs (an identity server beside the app, say) gets a preview of its own, for the
    /// browser that asked: from another device, "localhost" is that device. Any other address is left as it is, so
    /// a redirect can't put a port Fleet doesn't run on the network.
    /// </summary>
    private async Task<string> PreviewLocationAsync(string location, string browserHost, string proxyOrigin, Uri target)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Port != target.Port
            && LoopbackUrl.IsLoopbackHost(uri.Host)
            && _isAppPort(uri.Port))
        {
            try
            {
                var listener = await EnsureAsync(new Uri(uri.GetLeftPart(UriPartial.Authority)));
                return OriginFor(browserHost, listener) + uri.PathAndQuery + uri.Fragment;
            }
            catch (IOException)
            {
                // No listener for it: the browser gets the redirect as the app sent it.
            }
        }

        return RewriteLocation(location, proxyOrigin, target);
    }

    /// <summary>A redirect to the app itself stays in the preview. Redirects elsewhere are left as they are.</summary>
    internal static string RewriteLocation(string location, string proxyOrigin, Uri target)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Port != target.Port)
            return location;
        if (!LoopbackUrl.IsLoopbackHost(uri.Host))
            return location;

        return proxyOrigin + uri.PathAndQuery + uri.Fragment;
    }

    internal static string WithoutFrameAncestors(string policy)
        => string.Join("; ", policy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(directive => !directive.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase)));

    /// <summary>How to bind for Fleet's own <see cref="FleetOptions.Host"/>: loopback, every address, or one address.</summary>
    internal static (BindKind Kind, IPAddress Address) BindFor(string fleetHost)
    {
        var host = fleetHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return (BindKind.Localhost, IPAddress.Loopback);
        if (!IPAddress.TryParse(host, out var address) || host is "*" or "+")
            return (BindKind.Any, IPAddress.Any);
        if (IPAddress.IsLoopback(address))
            return (BindKind.Localhost, IPAddress.Loopback);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return (BindKind.Any, IPAddress.Any);
        return (BindKind.Address, address);
    }

    /// <summary><c>"41000-41099"</c> → (41000, 41099); empty or malformed → any free port.</summary>
    internal static (int First, int Last)? ParsePortRange(string? range)
    {
        var parts = (range ?? string.Empty).Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
               && first is > 0 and <= 65535 && last >= first && last <= 65535
            ? (first, last)
            : null;
    }

    private int ChoosePort(IPAddress address)
    {
        if (_portRange is not { } range)
            return FreePort(address, 0) ?? throw new IOException("No free port for a preview.");

        for (var port = range.First; port <= range.Last; port++)
        {
            if (_byOrigin.Values.Any(lazy => lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result.Listener.Port == port))
                continue;
            if (FreePort(address, port) is { } free)
                return free;
        }

        throw new IOException($"Every port in Fleet:Browser:PortRange ({range.First}-{range.Last}) is taken. Widen the range or close some previews.");
    }

    private static int? FreePort(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string ReplaceOrigin(string value, string from, string to)
        => value.StartsWith(from, StringComparison.OrdinalIgnoreCase) ? to + value[from.Length..] : value;

    [GeneratedRegex(@"'(?<urls>wss?://[^']+)'\.split\(','\);?")]
    private static partial Regex RefreshUrls();

    [GeneratedRegex(@";\s*domain=[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex CookieDomain();

    [GeneratedRegex(@";\s*secure\s*(?=;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CookieSecure();

    [GeneratedRegex(@";\s*samesite\s*=\s*none\s*(?=;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CookieSameSiteNone();

    [GeneratedRegex(@";\s*samesite\s*=[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex CookieSameSite();

    internal enum BindKind
    {
        Localhost,
        Any,
        Address,
    }

    /// <summary>A listener, and the refresh-server ports its pages' refresh scripts named.</summary>
    private sealed class Running(PreviewListener listener)
    {
        public PreviewListener Listener { get; } = listener;
        public ConcurrentDictionary<int, byte> RefreshPorts { get; } = new();
        public WebApplication App { get; set; } = null!;
    }
}
