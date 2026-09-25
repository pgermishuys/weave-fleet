using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>What the live tests with a real <c>opencode</c> share: a scratch OpenCode home and Fleet on Kestrel.</summary>
internal static class PooledOpenCodeLiveHost
{
    /// <summary>
    /// A user config that points OpenCode at the fake model and loads <paramref name="pluginPath"/> as a plugin of
    /// the user's own. Returns the env that points the pooled process at it. A <paramref name="contextLimit"/> gives the
    /// fake model a context window, so OpenCode compacts once a turn reports more tokens than that.
    /// </summary>
    public static Dictionary<string, string> WriteScratchOpenCodeHome(string root, Uri llmBaseUrl, string pluginPath, int? contextLimit = null)
    {
        var dirs = new Dictionary<string, string>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var dir in dirs.Values)
            Directory.CreateDirectory(dir);

        // A fresh HOME makes OpenCode fetch and install things on its first request, which can take
        // minutes. None of it matters here: the model is local and the plugins are files.
        dirs["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        dirs["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
        dirs["OPENCODE_DISABLE_MODELS_FETCH"] = "true";
        dirs["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
        dirs["OPENCODE_DISABLE_SHARE"] = "true";

        var configDir = Path.Combine(dirs["XDG_CONFIG_HOME"], "opencode");
        Directory.CreateDirectory(configDir);
        var baseUrl = llmBaseUrl.ToString().TrimEnd('/') + "/v1";
        var limit = contextLimit is { } context ? $$""", "limit": { "context": {{context}}, "output": 1000 }""" : "";
        File.WriteAllText(Path.Combine(configDir, "opencode.json"), $$"""
            {
              "provider": {
                "fake": {
                  "npm": "@ai-sdk/openai-compatible",
                  "options": { "baseURL": "{{baseUrl}}", "apiKey": "fake-key" },
                  "models": { "fake-model": { "tool_call": true{{limit}} } }
                }
              },
              "model": "fake/fake-model",
              "small_model": "fake/fake-model",
              "plugin": ["{{new Uri(pluginPath).AbsoluteUri}}"]
            }
            """);

        return dirs;
    }

    /// <summary>Fleet on a real Kestrel port, so the pooled process can call the bridge.</summary>
    internal sealed class KestrelFleetFactory(string dbPath) : WebApplicationFactory<Program>
    {
        private IHost? _host;

        public bool IsStarted => _host is not null;

        public IServiceProvider LiveServices => _host?.Services ?? throw new InvalidOperationException("Not started");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Fleet:DatabasePath", dbPath);
            builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.ChangeExtension(dbPath, ".analytics.db"));
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
            builder.UseSetting("Fleet:Host", "127.0.0.1");
            builder.UseSetting("Fleet:Port", "0");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "false");
            builder.ConfigureServices(services =>
            {
                // Warmup would start an extra opencode process before Fleet knows its port.
                foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(OpenCodeWarmupHostedService)).ToList())
                    services.Remove(descriptor);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureWebHost(web => web.UseKestrel());
            _host = builder.Build();
            _host.Start();
            _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ShouldNotBeEmpty();

            // A throwaway host for the base class, so it doesn't start a second app on the same database.
            return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureWebHost(web => web.UseTestServer())
                .Build();
        }

        public override async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            await base.DisposeAsync();
        }
    }
}
