using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

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

    private static readonly Action<ILogger, string, Exception?> LogPersistFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6, "PersistFailed"),
            "Failed to persist message event for session {SessionId}");

    private readonly OpenCodeHttpClient _httpClient;
    private readonly OpenCodeProcessManager _processManager;
    private readonly PortAllocator _portAllocator;
    private readonly int _allocatedPort;
    private readonly string _workingDirectory;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger<OpenCodeHarnessInstance> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _fleetSessionId;

    private string? _openCodeSessionId;
    private HarnessInstanceStatus _status = HarnessInstanceStatus.Starting;
    private bool _disposed;

    /// <summary>Initialises the instance with all required dependencies.</summary>
    public OpenCodeHarnessInstance(
        string instanceId,
        string fleetSessionId,
        OpenCodeHttpClient httpClient,
        OpenCodeProcessManager processManager,
        PortAllocator portAllocator,
        int allocatedPort,
        string workingDirectory,
        TimeSpan shutdownTimeout,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCodeHarnessInstance> logger,
        IAnalyticsCollector? analyticsCollector = null,
        string? projectId = null,
        string? projectName = null)
    {
        InstanceId = instanceId;
        _fleetSessionId = fleetSessionId;
        _httpClient = httpClient;
        _processManager = processManager;
        _portAllocator = portAllocator;
        _allocatedPort = allocatedPort;
        _workingDirectory = workingDirectory;
        _shutdownTimeout = shutdownTimeout;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _analyticsCollector = analyticsCollector;
        _projectId = projectId;
        _projectName = projectName;

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
            // Fire-and-forget analytics intercept — never blocks or throws
            if (_analyticsCollector is not null)
            {
                var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
                    sseEvt, _fleetSessionId, _projectId, _projectName, _workingDirectory);
                if (tokenEvent is not null)
                    _analyticsCollector.AcceptTokenEvent(tokenEvent);
            }

            var harnessEvent = OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId);

            // Fire-and-forget persistence (instance-owned — never blocks event stream)
            _ = TryPersistMessageAsync(harnessEvent);
            _ = TryPersistPartAsync(harnessEvent);

            yield return harnessEvent;
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
                _ = ex;
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

    private async Task TryPersistMessageAsync(HarnessEvent evt)
    {
        // Only process message.created events
        if (evt.Type is not "message.created")
            return;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return;

            var payload = evt.Payload.Value;

            // Guard: must have an "info" property
            if (!payload.TryGetProperty("info", out var infoEl))
                return;

            // Guard: only persist user and assistant messages.
            // Read role from raw JSON — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in STJ polymorphism.
            if (!infoEl.TryGetProperty("role", out var roleEl))
                return;

            var role = roleEl.GetString();
            if (role is not ("user" or "assistant"))
                return;

            // Deserialize the concrete info type directly (avoids STJ polymorphism ordering issues).
            OpenCodeMessageInfo? info = role == "assistant"
                ? infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default)
                : infoEl.Deserialize<OpenCodeUserMessage>(OpenCodeJsonOptions.Default);
            if (info is null) return;

            // Deserialize parts array separately
            IReadOnlyList<OpenCodeMessagePart> parts = [];
            if (payload.TryGetProperty("parts", out var partsEl))
                parts = partsEl.Deserialize<IReadOnlyList<OpenCodeMessagePart>>(OpenCodeJsonOptions.Default) ?? [];

            var openCodeMessage = new OpenCodeMessageWithParts { Info = info, Parts = parts };
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(openCodeMessage);

            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

            var persisted = MessagePersistenceService.ToPersistedMessage(_fleetSessionId, harnessMessage);

            // Don't overwrite existing message that may already have parts from message.part.updated
            if (harnessMessage.Parts.Count == 0)
            {
                var existing = await messageRepo.GetByIdAsync(persisted.Id, persisted.SessionId).ConfigureAwait(false);
                if (existing is not null) return; // Don't overwrite with empty skeleton
            }

            await messageRepo.UpsertAsync(persisted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event stream
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private async Task TryPersistPartAsync(HarnessEvent evt)
    {
        // Only process message.part.updated events
        if (evt.Type is not "message.part.updated")
            return;

        try
        {
            if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
                return;

            var payload = evt.Payload.Value;

            // Extract the "part" object from the payload
            if (!payload.TryGetProperty("part", out var partEl))
                return;

            if (partEl.ValueKind != JsonValueKind.Object)
                return;

            // Extract messageID (uppercase D) from the part object
            if (!partEl.TryGetProperty("messageID", out var messageIdEl))
                return;

            var messageId = messageIdEl.GetString();
            if (string.IsNullOrEmpty(messageId))
                return;

            // Extract the "type" discriminator from raw JSON before deserializing.
            // STJ [JsonPolymorphic] requires the discriminator to be the FIRST property in the
            // JSON object, but real OpenCode SSE payloads have "messageID" before "type".
            // Avoid the JsonException by extracting "type" manually and dispatching to the
            // concrete subtype — the same pattern TryPersistMessageAsync uses for "role".
            if (!partEl.TryGetProperty("type", out var typeEl))
                return;

            OpenCodeMessagePart? openCodePart = typeEl.GetString() switch
            {
                "text" => partEl.Deserialize<OpenCodeTextPart>(OpenCodeJsonOptions.Default),
                "tool" => partEl.Deserialize<OpenCodeToolPart>(OpenCodeJsonOptions.Default),
                "reasoning" => partEl.Deserialize<OpenCodeReasoningPart>(OpenCodeJsonOptions.Default),
                _ => null,
            };

            if (openCodePart is null)
                return; // Unsupported or unknown part type

            // Map to Fleet MessagePart
            var fleetPart = OpenCodeMapper.MapPart(openCodePart);
            if (fleetPart is null)
                return; // Mapper returned null (e.g. text part with null Text)

            using var scope = _scopeFactory.CreateScope();
            var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

            var existing = await messageRepo.GetByIdAsync(messageId, _fleetSessionId).ConfigureAwait(false);

            PersistedMessage persisted;
            if (existing is null)
            {
                // Create new skeleton message for this assistant part
                var partsJson = JsonSerializer.Serialize(
                    new[] { fleetPart }, MessagePersistenceService.SerializerOptions);
                persisted = new PersistedMessage
                {
                    Id = messageId,
                    SessionId = _fleetSessionId,
                    Role = "assistant",
                    PartsJson = partsJson,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                };
            }
            else
            {
                persisted = MessagePersistenceService.MergePart(existing, fleetPart);
            }

            await messageRepo.UpsertAsync(persisted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash event stream
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }
}
