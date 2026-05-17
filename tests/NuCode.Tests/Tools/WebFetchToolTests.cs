using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class WebFetchToolTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly WebFetchTool _sut;

    public WebFetchToolTests()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _sut = new WebFetchTool(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    // ── Basic properties ──

    [Fact]
    public void NameIsWebfetch()
    {
        _sut.Name.ShouldBe("webfetch");
    }

    [Fact]
    public void ToAIFunctionReturnsFunction()
    {
        var fn = _sut.ToAIFunction();
        fn.ShouldNotBeNull();
        fn.Name.ShouldBe("webfetch");
    }

    // ── Validation ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyUrlReturnsError(string? url)
    {
        var result = await InvokeAsync(url: url!);
        result.ShouldContain("url is required");
    }

    [Fact]
    public async Task InvalidUrlReturnsError()
    {
        var result = await InvokeAsync(url: "not-a-url");
        result.ShouldContain("Invalid URL");
    }

    [Fact]
    public async Task InvalidFormatReturnsError()
    {
        var result = await InvokeAsync(url: "https://example.com", format: "xml");
        result.ShouldContain("format must be");
    }

    [Fact]
    public async Task NonHttpSchemeReturnsError()
    {
        var result = await InvokeAsync(url: "ftp://example.com/file.txt");
        result.ShouldContain("Only HTTP/HTTPS URLs are supported");
    }

    // ── HTTP to HTTPS upgrade ──

    [Fact]
    public async Task HttpUrlUpgradedToHttps()
    {
        _handler.SetResponse("<html><body>Hello</body></html>");

        var result = await InvokeAsync(url: "http://example.com/page", format: "text");

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.RequestUri!.Scheme.ShouldBe("https");
    }

    // ── Format conversions ──

    [Fact]
    public async Task HtmlFormatReturnsRawHtml()
    {
        var html = "<html><body><h1>Title</h1><p>Hello world</p></body></html>";
        _handler.SetResponse(html);

        var result = await InvokeAsync(url: "https://example.com", format: "html");

        result.ShouldBe(html);
    }

    [Fact]
    public async Task TextFormatStripsHtmlTags()
    {
        _handler.SetResponse("<html><body><h1>Title</h1><p>Hello world</p></body></html>");

        var result = await InvokeAsync(url: "https://example.com", format: "text");

        result.ShouldNotContain("<h1>");
        result.ShouldNotContain("<p>");
        result.ShouldContain("Title");
        result.ShouldContain("Hello world");
    }

    [Fact]
    public async Task TextFormatDecodesHtmlEntities()
    {
        _handler.SetResponse("<p>A &amp; B &lt; C</p>");

        var result = await InvokeAsync(url: "https://example.com", format: "text");

        result.ShouldContain("A & B < C");
    }

    [Fact]
    public async Task MarkdownFormatConvertsHtmlToMarkdown()
    {
        _handler.SetResponse("<html><body><h1>Title</h1><p>Hello <strong>world</strong></p></body></html>");

        var result = await InvokeAsync(url: "https://example.com", format: "markdown");

        // ReverseMarkdown should produce markdown headings and bold
        result.ShouldContain("Title");
        result.ShouldContain("**world**");
    }

    [Fact]
    public async Task DefaultFormatIsMarkdown()
    {
        _handler.SetResponse("<html><body><h1>Title</h1></body></html>");

        var result = await InvokeAsync(url: "https://example.com");

        // Should be markdown (same as explicit markdown format)
        result.ShouldContain("Title");
    }

    // ── Image URLs ──

    [Fact]
    public async Task ImageUrlReturnsBase64()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        _handler.SetResponse(imageBytes, "image/png");

        var result = await InvokeAsync(url: "https://example.com/photo.png");

        result.ShouldStartWith("[image: data:image/png;base64,");
        result.ShouldContain(Convert.ToBase64String(imageBytes));
    }

    [Fact]
    public async Task JpegImageUrlReturnsBase64()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF };
        _handler.SetResponse(imageBytes, "image/jpeg");

        var result = await InvokeAsync(url: "https://example.com/photo.jpg");

        result.ShouldStartWith("[image: data:image/jpeg;base64,");
    }

    // ── Size limit ──

    [Fact]
    public async Task ResponseExceedingSizeLimitReturnsError()
    {
        // Create a response larger than 5MB
        var largeContent = new string('x', 5 * 1024 * 1024 + 1);
        _handler.SetResponse(largeContent);

        var result = await InvokeAsync(url: "https://example.com/large");

        result.ShouldContain("too large");
    }

    [Fact]
    public async Task ContentLengthHeaderExceedingLimitReturnsError()
    {
        _handler.SetResponse("small", contentLength: 6 * 1024 * 1024);

        var result = await InvokeAsync(url: "https://example.com/large");

        result.ShouldContain("too large");
    }

    // ── Cloudflare retry ──

    [Fact]
    public async Task CloudflareBlockRetries()
    {
        var callCount = 0;
        _handler.SetResponseFactory(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                var response403 = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("blocked"),
                };
                response403.Headers.Add("cf-mitigated", "challenge");
                return response403;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<p>Success after retry</p>"),
            };
        });

        var result = await InvokeAsync(url: "https://example.com", format: "text");

        callCount.ShouldBe(2);
        result.ShouldContain("Success after retry");
    }

    // ── HTTP errors ──

    [Fact]
    public async Task HttpErrorReturnsErrorMessage()
    {
        _handler.SetStatusCode(HttpStatusCode.InternalServerError);

        var result = await InvokeAsync(url: "https://example.com");

        result.ShouldContain("Error fetching URL");
    }

    [Fact]
    public async Task NotFoundReturnsErrorMessage()
    {
        _handler.SetStatusCode(HttpStatusCode.NotFound);

        var result = await InvokeAsync(url: "https://example.com/missing");

        result.ShouldContain("Error fetching URL");
    }

    // ── Timeout ──

    [Fact]
    public async Task TimeoutReturnsErrorMessage()
    {
        _handler.SetDelay(TimeSpan.FromSeconds(5));
        _handler.SetResponse("slow response");

        // Use a 1-second timeout
        var result = await InvokeAsync(url: "https://example.com", timeout: 1);

        result.ShouldContain("timed out");
    }

    [Fact]
    public async Task TimeoutClampedToMaximum()
    {
        _handler.SetResponse("<p>fast</p>");

        // Request 999 seconds — should be clamped to 120
        var result = await InvokeAsync(url: "https://example.com", timeout: 999);

        // Should still succeed (the clamp just limits the max, it doesn't cause a timeout)
        result.ShouldNotContain("Error");
    }

    // ── Helpers ──

    private async Task<string> InvokeAsync(
        string url,
        string? format = null,
        int? timeout = null)
    {
        var fn = _sut.ToAIFunction();
        var args = new Dictionary<string, object?> { ["url"] = url };
        if (format is not null)
        {
            args["format"] = format;
        }
        if (timeout is not null)
        {
            args["timeout"] = timeout;
        }

        var result = await fn.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
        return result?.ToString() ?? "";
    }

    /// <summary>
    /// Test double for HttpMessageHandler that allows controlling responses.
    /// </summary>
    internal sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;
        private TimeSpan _delay = TimeSpan.Zero;

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(string content, long? contentLength = null)
        {
            _responseFactory = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "text/html"),
                };
                if (contentLength.HasValue)
                {
                    response.Content.Headers.ContentLength = contentLength.Value;
                }
                return response;
            };
        }

        public void SetResponse(byte[] content, string contentType)
        {
            _responseFactory = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                return response;
            };
        }

        public void SetStatusCode(HttpStatusCode statusCode)
        {
            _responseFactory = _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(""),
            };
        }

        public void SetResponseFactory(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responseFactory = factory;
        }

        public void SetDelay(TimeSpan delay)
        {
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            if (_responseFactory is not null)
            {
                return _responseFactory(request);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            };
        }
    }
}
