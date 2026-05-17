using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace NuCode.Mcp;

/// <summary>
/// Manages MCP server connections, tracks their status, and provides tools from connected servers.
/// </summary>
internal sealed class McpManager : IMcpManager
{
    private readonly ConcurrentDictionary<string, McpServerConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMcpClientWrapper> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerState> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _restartCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _monitorCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconnectLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly IMcpClientFactory _clientFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;

    public event Action<McpServerState>? ServerStateChanged;

    public McpManager(IEnumerable<McpServerConfig> configs, ILoggerFactory? loggerFactory)
        : this(configs, new McpClientFactory(), loggerFactory)
    {
    }

    internal McpManager(IEnumerable<McpServerConfig> configs, IMcpClientFactory clientFactory, ILoggerFactory? loggerFactory)
    {
        _clientFactory = clientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpManager>();

        foreach (var config in configs)
        {
            _configs[config.Name] = config;
            SetStatus(config.Name, McpServerStatus.Disabled);
        }
    }

    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        var tasks = _configs.Values
            .Where(c => c.Enabled)
            .Select(c => ConnectCoreAsync(c, cancellationToken));

        await Task.WhenAll(tasks);
    }

    public async Task ConnectAsync(string name, CancellationToken cancellationToken)
    {
        if (!_configs.TryGetValue(name, out var config))
        {
            throw new InvalidOperationException($"No MCP server configured with name '{name}'.");
        }

        await ConnectCoreAsync(config, cancellationToken);
    }

    public async Task DisconnectAsync(string name, CancellationToken cancellationToken)
    {
        CancelMonitor(name);

        if (_clients.TryRemove(name, out var client))
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing MCP client '{Name}'", name);
            }
        }

        SetStatus(name, McpServerStatus.Disabled);
    }

    public IReadOnlyDictionary<string, McpServerState> GetStatus()
    {
        return _status.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();

        foreach (var (name, client) in _clients)
        {
            if (_status.TryGetValue(name, out var state) && state.Status != McpServerStatus.Connected)
            {
                continue;
            }

            try
            {
                var mcpTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                tools.AddRange(mcpTools);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to list tools from MCP server '{Name}'", name);
                SetStatus(name, McpServerStatus.Failed, ex.Message);
            }
        }

        return tools.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<AITool>>> GetToolsByServerAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<AITool>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, client) in _clients)
        {
            if (_status.TryGetValue(name, out var state) && state.Status != McpServerStatus.Connected)
            {
                continue;
            }

            try
            {
                var mcpTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                result[name] = mcpTools.ToList().AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to list tools from MCP server '{Name}'", name);
                SetStatus(name, McpServerStatus.Failed, ex.Message);
            }
        }

        return result;
    }

    public async Task AddAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        _configs[config.Name] = config;

        if (config.Enabled)
        {
            await ConnectCoreAsync(config, cancellationToken);
        }
        else
        {
            SetStatus(config.Name, McpServerStatus.Disabled);
        }
    }

    public async Task<McpServerState> CheckHealthAsync(string name, CancellationToken cancellationToken)
    {
        if (!_status.TryGetValue(name, out var currentState))
        {
            throw new InvalidOperationException($"No MCP server configured with name '{name}'.");
        }

        if (currentState.Status != McpServerStatus.Connected)
        {
            return currentState;
        }

        if (!_clients.TryGetValue(name, out var client))
        {
            return currentState;
        }

        try
        {
            await client.PingAsync(cancellationToken: cancellationToken);
            return currentState;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check failed for MCP server '{Name}', triggering reconnect", name);

            if (_configs.TryGetValue(name, out var config) && config.AutoReconnect)
            {
                _ = Task.Run(() => ReconnectAsync(config), CancellationToken.None);
            }
            else
            {
                SetStatus(name, McpServerStatus.Failed, ex.Message);
            }

            return _status[name];
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts) in _monitorCts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        _monitorCts.Clear();

        foreach (var (name, client) in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing MCP client '{Name}' during shutdown", name);
            }
        }

        _clients.Clear();
        _connectLock.Dispose();

        foreach (var (_, semaphore) in _reconnectLocks)
        {
            semaphore.Dispose();
        }

        _reconnectLocks.Clear();
    }

    private async Task ConnectCoreAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        CancelMonitor(config.Name);

        // Disconnect existing if reconnecting
        if (_clients.TryRemove(config.Name, out var existing))
        {
            try
            {
                await existing.DisposeAsync();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        try
        {
            var client = await _clientFactory.CreateAsync(config, _loggerFactory, cancellationToken);

            _clients[config.Name] = client;
            _restartCounts[config.Name] = 0;
            SetStatus(config.Name, McpServerStatus.Connected);

            _logger?.LogInformation("Connected to MCP server '{Name}'", config.Name);

            if (config.AutoReconnect)
            {
                StartMonitor(config);
            }
        }
        catch (Exception ex)
        {
            SetStatus(config.Name, McpServerStatus.Failed, ex.Message);
            _logger?.LogWarning(ex, "Failed to connect to MCP server '{Name}'", config.Name);
        }
    }

    private void StartMonitor(McpServerConfig config)
    {
        var cts = new CancellationTokenSource();
        if (_monitorCts.TryRemove(config.Name, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        _monitorCts[config.Name] = cts;
        _ = Task.Run(() => MonitorConnectionAsync(config, cts.Token), CancellationToken.None);
    }

    private async Task MonitorConnectionAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_clients.TryGetValue(config.Name, out var client))
            {
                return;
            }

            try
            {
                await client.PingAsync(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Connection monitor detected failure for MCP server '{Name}'", config.Name);
                await ReconnectAsync(config);
                return;
            }
        }
    }

    private async Task ReconnectAsync(McpServerConfig config)
    {
        var semaphore = _reconnectLocks.GetOrAdd(config.Name, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(TimeSpan.Zero))
        {
            _logger?.LogDebug("Reconnect already in progress for MCP server '{Name}', skipping", config.Name);
            return;
        }

        try
        {
            await ReconnectCoreAsync(config);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ReconnectCoreAsync(McpServerConfig config)
    {
        var restartCount = _restartCounts.GetOrAdd(config.Name, 0);
        var maxRestarts = Math.Min(config.MaxRestarts, 10);

        while (restartCount < maxRestarts)
        {
            restartCount++;
            _restartCounts[config.Name] = restartCount;
            SetStatus(config.Name, McpServerStatus.Reconnecting);

            var delaySeconds = (int)Math.Pow(2, restartCount);
            _logger?.LogInformation(
                "Reconnecting to MCP server '{Name}' (attempt {Attempt}/{Max}) after {Delay}s",
                config.Name, restartCount, config.MaxRestarts, delaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            // Dispose existing client
            if (_clients.TryRemove(config.Name, out var existing))
            {
                try
                {
                    await existing.DisposeAsync();
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            try
            {
                var client = await _clientFactory.CreateAsync(config, _loggerFactory, CancellationToken.None);
                _clients[config.Name] = client;
                _restartCounts[config.Name] = 0;
                SetStatus(config.Name, McpServerStatus.Connected);

                _logger?.LogInformation("Successfully reconnected to MCP server '{Name}'", config.Name);

                StartMonitor(config);
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reconnect attempt {Attempt} failed for MCP server '{Name}'", restartCount, config.Name);
            }
        }

        // Max retries exceeded
        SetStatus(config.Name, McpServerStatus.Failed, "Max reconnect attempts exceeded");
        _logger?.LogError("MCP server '{Name}' exceeded max reconnect attempts ({Max})", config.Name, maxRestarts);
    }

    private void SetStatus(string name, McpServerStatus status, string? error = null)
    {
        _restartCounts.TryGetValue(name, out var restartCount);
        var maxRestarts = _configs.TryGetValue(name, out var config) ? Math.Min(config.MaxRestarts, 10) : 3;

        var state = new McpServerState(name, status, error, restartCount, maxRestarts);
        _status[name] = state;

        try
        {
            ServerStateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ServerStateChanged handler threw for MCP server '{Name}'", name);
        }
    }

    private void CancelMonitor(string name)
    {
        if (_monitorCts.TryRemove(name, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
