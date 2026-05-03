using ModelContextProtocol.Client;

namespace NuCode.Mcp;

/// <summary>
/// Default wrapper around a real <see cref="McpClient"/>.
/// </summary>
internal sealed class McpClientWrapper : IMcpClientWrapper
{
    private readonly McpClient _client;

    internal McpClientWrapper(McpClient client)
    {
        _client = client;
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await _client.PingAsync(cancellationToken: cancellationToken);
    }

    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        return await _client.ListToolsAsync(cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
