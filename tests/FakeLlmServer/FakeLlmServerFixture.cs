namespace FakeLlmServer;

/// <summary>
/// Starts the FakeLlmServer as an in-process <see cref="WebApplication"/> on a random port.
/// Used by test fixtures to intercept LLM API calls without real network access.
/// </summary>
public sealed class FakeLlmServerFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeLlmServerFixture(WebApplication app, Uri baseUrl, ScriptedResponseStore queue)
    {
        _app = app;
        BaseUrl = baseUrl;
        Queue = queue;
    }

    /// <summary>The base URL of the running fake server (e.g. http://127.0.0.1:12345).</summary>
    public Uri BaseUrl { get; }

    /// <summary>The queue of scripted responses. Enqueue responses before sending prompts.</summary>
    public ScriptedResponseStore Queue { get; }

    /// <summary>
    /// Starts the fake server on a random available port and returns the fixture.
    /// </summary>
    public static async Task<FakeLlmServerFixture> StartAsync()
    {
        var queue = new ScriptedResponseStore();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(queue);

        var app = builder.Build();
        app.MapOpenAiEndpoints();

        await app.StartAsync();

        // Resolve the actual bound port
        var addresses = app.Urls;
        var boundUrl = addresses.First();
        var baseUrl = new Uri(boundUrl);

        return new FakeLlmServerFixture(app, baseUrl, queue);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
