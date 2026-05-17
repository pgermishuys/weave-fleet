using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NuCode.Configuration;

namespace NuCode.Tools;

/// <summary>
/// Provides timeout enforcement middleware for the Microsoft Agent Framework function invocation pipeline.
/// Wraps each tool call with a per-tool or global timeout; on expiry returns a user-friendly message
/// rather than propagating <see cref="OperationCanceledException"/>.
/// </summary>
internal static class TimeoutMiddleware
{
    /// <summary>
    /// Default timeout in milliseconds when no configuration is provided.
    /// </summary>
    internal const int DefaultTimeoutMs = 30_000;

    /// <summary>
    /// Creates a function invocation middleware delegate that enforces per-tool timeouts.
    /// Use with <c>agent.AsBuilder().Use(TimeoutMiddleware.CreateMiddleware(configMonitor))</c>.
    /// </summary>
    /// <param name="configMonitor">Monitor for the current NuCode configuration.</param>
    /// <returns>
    /// A middleware delegate compatible with
    /// <c>FunctionInvocationDelegatingAgentBuilderExtensions.Use</c>.
    /// </returns>
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> CreateMiddleware(
        IOptionsMonitor<NuCodeConfig> configMonitor)
    {
        return async (_, context, next, cancellationToken) =>
        {
            var toolName = context.Function.Name;
            var timeoutMs = ResolveTimeoutMs(toolName, configMonitor.CurrentValue.Timeout);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                return await next(context, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return $"Tool '{toolName}' timed out after {timeoutMs}ms. Consider breaking the operation into smaller steps.";
            }
        };
    }

    /// <summary>
    /// Resolves the effective timeout in milliseconds for a given tool name,
    /// respecting per-tool overrides then the global default.
    /// </summary>
    /// <param name="toolName">The name of the tool being invoked.</param>
    /// <param name="config">The timeout configuration, or <see langword="null"/> to use the default.</param>
    /// <returns>The timeout in milliseconds to apply.</returns>
    internal static int ResolveTimeoutMs(string toolName, TimeoutConfig? config)
    {
        if (config?.ToolOverrides is not null &&
            config.ToolOverrides.TryGetValue(toolName, out var toolSpecific))
        {
            return toolSpecific;
        }

        return config?.DefaultMs ?? DefaultTimeoutMs;
    }
}
