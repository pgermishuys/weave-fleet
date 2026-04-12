using System.Text.Json;

namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// A running bridge to an AI agent, one per session.
/// Implementations are created by <c>IHarnessRuntime.SpawnAsync</c>.
/// </summary>
public interface IHarnessSession : IAsyncDisposable
{
    /// <summary>Unique identifier for this running instance.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Opaque token used to resume this session after a crash or restart.
    /// Null if the harness has not yet captured a session ID or does not support resume.
    /// </summary>
    string? ResumeToken { get; }

    /// <summary>The harness type that created this instance (e.g. "opencode").</summary>
    string HarnessType { get; }

    /// <summary>Current lifecycle status.</summary>
    HarnessSessionStatus Status { get; }

    /// <summary>Gracefully stop the agent process.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Permanently purge remote session state, then stop the agent process.</summary>
    Task DeleteAsync(CancellationToken ct);

    /// <summary>Send a user prompt to the agent.</summary>
    Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct);

    /// <summary>Execute a slash command on the agent.</summary>
    Task SendCommandAsync(CommandOptions options, CancellationToken ct);

    /// <summary>Abort the current agent operation.</summary>
    Task AbortAsync(CancellationToken ct);

    /// <summary>Retrieve the message history for this instance.</summary>
    Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct);

    /// <summary>Subscribe to a real-time stream of harness events.</summary>
    IAsyncEnumerable<HarnessEvent> SubscribeAsync(CancellationToken ct);

    /// <summary>Check whether this instance is still healthy.</summary>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct);

    /// <summary>List available agents for this instance.</summary>
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct);

    /// <summary>List available slash commands for this instance.</summary>
    Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct);

    /// <summary>List available model providers for this instance.</summary>
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct);
}
