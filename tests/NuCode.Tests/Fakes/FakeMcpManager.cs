using Microsoft.Extensions.AI;
using NuCode.Mcp;

namespace NuCode.Fakes;

internal sealed class FakeMcpManager : IMcpManager
{
    private IReadOnlyDictionary<string, IReadOnlyList<AITool>> _toolsByServer =
        new Dictionary<string, IReadOnlyList<AITool>>();

    private IReadOnlyDictionary<string, McpServerState> _status =
        new Dictionary<string, McpServerState>();

    public int ConnectAllCallCount { get; private set; }

    public void SetToolsByServer(IReadOnlyDictionary<string, IReadOnlyList<AITool>> toolsByServer) =>
        _toolsByServer = toolsByServer;

    public void SetStatus(IReadOnlyDictionary<string, McpServerState> status) =>
        _status = status;

    public Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        ConnectAllCallCount++;
        return Task.CompletedTask;
    }

    public Task ConnectAsync(string name, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DisconnectAsync(string name, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public IReadOnlyDictionary<string, McpServerState> GetStatus() => _status;

    public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AITool>>(_toolsByServer.Values.SelectMany(t => t).ToList());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<AITool>>> GetToolsByServerAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(_toolsByServer);

    public Task AddAsync(McpServerConfig config, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public event Action<McpServerState>? ServerStateChanged
    {
        add { }
        remove { }
    }

    public Task<McpServerState> CheckHealthAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(new McpServerState(name, McpServerStatus.Connected));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
