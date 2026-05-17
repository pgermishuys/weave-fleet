using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace NuCode.Mcp;

/// <summary>
/// Default factory that creates MCP clients using the real transport layer.
/// </summary>
internal sealed class McpClientFactory : IMcpClientFactory
{
    public async Task<IMcpClientWrapper> CreateAsync(
        McpServerConfig config,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        var transport = CreateTransport(config);

        return new McpClientWrapper(await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new() { Name = "NuCode", Version = "1.0.0" },
            },
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken));
    }

    private static IClientTransport CreateTransport(McpServerConfig config)
    {
        return config.Transport switch
        {
            McpTransport.Stdio => CreateStdioTransport(config),
            McpTransport.Http => CreateHttpTransport(config),
            _ => throw new ArgumentOutOfRangeException(nameof(config), $"Unsupported transport: {config.Transport}"),
        };
    }

    private static StdioClientTransport CreateStdioTransport(McpServerConfig config)
    {
        if (config.Command.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                $"MCP server '{config.Name}' has stdio transport but no command configured.");
        }

        var options = new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command[0],
            Arguments = config.Command.Length > 1
                ? [.. config.Command.Skip(1)]
                : [],
        };

        if (config.Environment is not null)
        {
            foreach (var (key, value) in config.Environment)
            {
                options.EnvironmentVariables ??= new Dictionary<string, string?>();
                options.EnvironmentVariables[key] = value;
            }
        }

        return new StdioClientTransport(options);
    }

    private static HttpClientTransport CreateHttpTransport(McpServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new InvalidOperationException(
                $"MCP server '{config.Name}' has HTTP transport but no URL configured.");
        }

        var options = new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = new Uri(config.Url),
        };

        if (config.Headers is not null)
        {
            foreach (var (key, value) in config.Headers)
            {
                options.AdditionalHeaders ??= new Dictionary<string, string>();
                options.AdditionalHeaders[key] = value;
            }
        }

        return new HttpClientTransport(options);
    }
}
