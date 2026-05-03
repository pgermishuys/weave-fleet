using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ReverseMarkdown;

namespace NuCode.Tools;

/// <summary>
/// Fetches content from a URL and returns it as markdown, plain text, or HTML.
/// Supports Cloudflare retry, size limits, and image detection.
/// </summary>
internal sealed partial class WebFetchTool(HttpClient httpClient) : INuCodeTool
{
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 120;

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
    };

    public string Name => "webfetch";

    public string Description => "Fetches content from a specified URL and returns it as markdown, plain text, or HTML.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Fetches content from a specified URL and returns it as markdown, plain text, or HTML.")]
    internal async Task<string> ExecuteAsync(
        [Description("The URL to fetch content from")] string url,
        [Description("The format to return the content in: 'markdown' (default), 'text', or 'html'")] string? format = null,
        [Description("Optional timeout in seconds (default 30, max 120)")] int? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Error: url is required.";
        }

        // Normalize format
        var outputFormat = (format ?? "markdown").Trim().ToLowerInvariant();
        if (outputFormat is not ("markdown" or "text" or "html"))
        {
            return "Error: format must be 'markdown', 'text', or 'html'.";
        }

        // Parse and validate URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"Error: Invalid URL: {url}";
        }

        // Upgrade HTTP to HTTPS
        if (uri.Scheme == "http")
        {
            uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        }

        if (uri.Scheme != "https")
        {
            return $"Error: Only HTTP/HTTPS URLs are supported. Got: {uri.Scheme}";
        }

        // Check if URL points to an image
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (s_imageExtensions.Contains(extension))
        {
            return await FetchImageAsBase64Async(uri, timeout, cancellationToken);
        }

        // Resolve timeout
        var timeoutSeconds = Math.Min(timeout ?? DefaultTimeoutSeconds, MaxTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var html = await FetchHtmlAsync(uri, cts.Token);
            return ConvertContent(html, outputFormat);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Error: Request timed out after {timeoutSeconds} seconds.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error fetching URL: {ex.Message}";
        }
    }

    private async Task<string> FetchHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; NuCode/1.0)");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Cloudflare retry: if 403 with cf-mitigated header, retry once
        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.Contains("cf-mitigated"))
        {
            response.Dispose();
            return await RetryFetchAsync(uri, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await ReadResponseWithLimitAsync(response, cancellationToken);
    }

    private async Task<string> RetryFetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var retryRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        retryRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        retryRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; NuCode/1.0)");

        using var retryResponse = await httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        retryResponse.EnsureSuccessStatusCode();
        return await ReadResponseWithLimitAsync(retryResponse, cancellationToken);
    }

    private static async Task<string> ReadResponseWithLimitAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Check Content-Length if available
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return $"Error: Response too large ({response.Content.Headers.ContentLength} bytes). Maximum is {MaxResponseBytes} bytes.";
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponseBytes + 1];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead > MaxResponseBytes)
        {
            return $"Error: Response too large (exceeded {MaxResponseBytes} bytes). Content truncated.";
        }

        // Detect encoding from Content-Type header, default to UTF-8
        var encoding = GetEncoding(response.Content.Headers.ContentType?.CharSet) ?? Encoding.UTF8;
        return encoding.GetString(buffer, 0, totalRead);
    }

    private static Encoding? GetEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<string> FetchImageAsBase64Async(Uri uri, int? timeout, CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Min(timeout ?? DefaultTimeoutSeconds, MaxTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; NuCode/1.0)");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var bytes = await ReadBytesWithLimitAsync(response, cts.Token);
            if (bytes is null)
            {
                return $"Error: Image too large (exceeded {MaxResponseBytes} bytes).";
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            var base64 = Convert.ToBase64String(bytes);
            return $"[image: data:{contentType};base64,{base64}]";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Error: Request timed out after {timeoutSeconds} seconds.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error fetching image: {ex.Message}";
        }
    }

    private static async Task<byte[]?> ReadBytesWithLimitAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponseBytes + 1];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead > MaxResponseBytes)
        {
            return null;
        }

        return buffer[..totalRead];
    }

    private static string ConvertContent(string html, string format)
    {
        return format switch
        {
            "html" => html,
            "text" => HtmlToText(html),
            "markdown" => HtmlToMarkdown(html),
            _ => html,
        };
    }

    private static string HtmlToMarkdown(string html)
    {
        var converter = new Converter(new Config
        {
            UnknownTags = Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
        });
        return converter.Convert(html).Trim();
    }

    private static string HtmlToText(string html)
    {
        // Simple HTML to text: strip tags, decode entities, collapse whitespace
        var text = StripHtmlTagsRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = CollapseWhitespaceRegex().Replace(text, " ");
        return text.Trim();
    }

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex StripHtmlTagsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();
}
