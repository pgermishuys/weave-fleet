using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Wraps a running <c>opencode serve</c> process and HTTP client for a single session.
/// Implements <see cref="IHarnessInstance"/>.
/// </summary>
internal sealed class OpenCodeHarnessInstance : IHarnessInstance
{
    private static readonly Action<ILogger, string, Exception?> LogSendPrompt =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "SendPrompt"),
            "Sending prompt to OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogAbort =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "Abort"),
            "Aborting OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogStop =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Stop"),
            "Stopping OpenCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, int, Exception?> LogProcessExited =
        LoggerMessage.Define<string, int>(LogLevel.Error, new EventId(4, "ProcessExited"),
            "OpenCode instance {InstanceId} process exited unexpectedly with code {ExitCode}.");

    private static readonly Action<ILogger, string, Exception?> LogSessionCreated =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, "SessionCreated"),
            "Created OpenCode session {SessionId}.");

    private readonly OpenCodeHttpClient _httpClient;
    private readonly OpenCodeProcessManager _processManager;
    private readonly PortAllocator _portAllocator;
    private readonly int _allocatedPort;
    private readonly string _workingDirectory;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger<OpenCodeHarnessInstance> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _openCodeSessionId;
    private HarnessInstanceStatus _status = HarnessInstanceStatus.Starting;
    private bool _disposed;

    /// <summary>Initialises the instance with all required dependencies.</summary>
    public OpenCodeHarnessInstance(
        string instanceId,
        OpenCodeHttpClient httpClient,
        OpenCodeProcessManager processManager,
        PortAllocator portAllocator,
        int allocatedPort,
        string workingDirectory,
        TimeSpan shutdownTimeout,
        ILogger<OpenCodeHarnessInstance> logger)
    {
        InstanceId = instanceId;
        _httpClient = httpClient;
        _processManager = processManager;
        _portAllocator = portAllocator;
        _allocatedPort = allocatedPort;
        _workingDirectory = workingDirectory;
        _shutdownTimeout = shutdownTimeout;
        _logger = logger;

        _status = HarnessInstanceStatus.Idle;

        // Subscribe to unexpected process exit
        _processManager.ProcessExited += OnProcessExited;
    }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public string HarnessType => "opencode";

    /// <inheritdoc />
    public HarnessInstanceStatus Status => _status;

    // -----------------------------------------------------------------------
    // IHarnessInstance
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);

        var parts = new List<OpenCodePromptPart>
        {
            new OpenCodePromptTextPart { Text = text },
        };

        if (options?.Attachments is { Count: > 0 } attachments)
        {
            foreach (var attachment in attachments)
            {
                parts.Add(new OpenCodePromptFilePart
                {
                    Mime = attachment.Mime,
                    Url = $"data:{attachment.Mime};base64,{attachment.Data}",
                    Filename = attachment.Filename,
                });
            }
        }

        OpenCodeModelRef? modelRef = null;
        if (options?.ModelId is { } modelId)
        {
            var slash = modelId.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                modelRef = new OpenCodeModelRef
                {
                    ProviderId = modelId[..slash],
                    ModelId = modelId[(slash + 1)..],
                };
            }
            else
            {
                modelRef = new OpenCodeModelRef { ProviderId = modelId, ModelId = modelId };
            }
        }

        var request = new OpenCodePromptRequest
        {
            Parts = parts,
            Agent = options?.Agent,
            Model = modelRef,
        };

        LogSendPrompt(_logger, InstanceId, null);
        await _httpClient.SendPromptAsyncFireAndForget(
            _openCodeSessionId!,
            request,
            _workingDirectory,
            ct).ConfigureAwait(false);

        _status = HarnessInstanceStatus.Running;
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        if (_openCodeSessionId is null)
        {
            return new MessagePage([], false);
        }

        var raw = await _httpClient.GetMessagesAsync(
            _openCodeSessionId,
            _workingDirectory,
            query?.Limit,
            query?.Before,
            ct).ConfigureAwait(false);

        var messages = OpenCodeMapper.ToHarnessMessages(raw);

        // OpenCode doesn't return a hasMore flag on this endpoint; use limit as heuristic.
        bool hasMore = query?.Limit.HasValue == true && raw.Count >= query.Limit.Value;

        return new MessagePage(messages, hasMore);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sseEvt in _httpClient
            .SubscribeToEventsAsync(_workingDirectory, ct)
            .ConfigureAwait(false))
        {
            yield return OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId);
        }
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken ct)
    {
        if (_openCodeSessionId is null) return;

        LogAbort(_logger, InstanceId, null);
        await _httpClient.AbortAsync(_openCodeSessionId, _workingDirectory, ct).ConfigureAwait(false);
        _status = HarnessInstanceStatus.Idle;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        if (!_processManager.IsRunning)
        {
            return new HealthCheckResult(false, "Process exited.");
        }

        try
        {
            var health = await _httpClient.CheckHealthAsync(ct).ConfigureAwait(false);
            return new HealthCheckResult(health.Healthy, health.Version is not null ? $"v{health.Version}" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        _status = HarnessInstanceStatus.Stopping;
        LogStop(_logger, InstanceId, null);

        // Best-effort: delete OpenCode session
        if (_openCodeSessionId is not null)
        {
            try
            {
                await _httpClient.DeleteSessionAsync(_openCodeSessionId, _workingDirectory, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort — don't let this block shutdown
            }
        }

        await _processManager.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        _status = HarnessInstanceStatus.Stopped;

        if (_allocatedPort > 0)
        {
            _portAllocator.ReleasePort(_allocatedPort);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _processManager.ProcessExited -= OnProcessExited;

        if (_status is not HarnessInstanceStatus.Stopped and not HarnessInstanceStatus.Error)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort on dispose
            }
        }

        await _processManager.DisposeAsync().ConfigureAwait(false);
        _sessionLock.Dispose();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_openCodeSessionId is not null) return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_openCodeSessionId is null)
            {
                var session = await _httpClient.CreateSessionAsync(null, _workingDirectory, ct)
                    .ConfigureAwait(false);
                _openCodeSessionId = session.Id;
                LogSessionCreated(_logger, session.Id, null);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        if (_status is HarnessInstanceStatus.Stopping or HarnessInstanceStatus.Stopped) return;

        LogProcessExited(_logger, InstanceId, exitCode, null);
        _status = HarnessInstanceStatus.Error;
    }
}
