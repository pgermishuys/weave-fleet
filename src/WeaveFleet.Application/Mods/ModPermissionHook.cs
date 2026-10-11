using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Sessions.Asks;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Mods;

/// <summary>What the event relay asks of the user's mods about an ask that would otherwise reach the user.</summary>
public interface IModPermissionHook
{
    /// <summary>
    /// The words the agent is told when a mod refuses the ask (<c>push-guard: Don't push from main.</c>), or
    /// <see langword="null"/> when none does and the ask goes to the user. Waits at most <see cref="ModPermissionHook.Wait"/>.
    /// </summary>
    Task<string?> RefusalAsync(string userId, string shownOn, PermissionAsk ask, CancellationToken ct);

    /// <summary>Answers the ask as the user's reject-with-a-message would. Never throws.</summary>
    Task RejectAsync(string userId, PermissionAsk ask, string message);
}

/// <summary>
/// Lets the user's mods refuse an agent's ask (<c>permission.ask</c>, <c>docs/mods/api.md</c>, "Events"). Fleet sends it
/// only for asks that would reach the user, so every harness gets it. It never blocks or breaks an ask: whatever goes
/// wrong, the ask goes to the user as it would have.
/// </summary>
public sealed partial class ModPermissionHook(
    IModHost host,
    IServiceScopeFactory scopes,
    IBackgroundUserScope users,
    TimeProvider time,
    ILogger<ModPermissionHook> logger) : IModPermissionHook
{
    /// <summary>How long Fleet waits for the whole chain. The host gives each hook less (1.5 s).</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    /// <summary>The longest reason Fleet passes on.</summary>
    public const int MaxReasonLength = 500;

    public async Task<string?> RefusalAsync(string userId, string shownOn, PermissionAsk ask, CancellationToken ct)
    {
        try
        {
            var dispatch = host.DispatchAsync(userId, new ModDispatchRequest("permission.ask", shownOn, Event(shownOn, ask)), ct);
            ModDispatchResult answer;
            try
            {
                answer = await dispatch.WaitAsync(Wait, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The host is left alone; the dispatch finishes on its own and its outcome is dropped.
                _ = dispatch.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                LogTimedOut(Wait.TotalSeconds);
                return null;
            }

            return answer.Dispatched ? Read(answer.Result) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            return null;
        }
    }

    public async Task RejectAsync(string userId, PermissionAsk ask, string message)
    {
        try
        {
            using var asUser = users.Begin(userId);
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var reply = await scope.ServiceProvider.GetRequiredService<SessionAsks>()
                    .ReplyToPermissionAsync(ask.SessionId, ask.Id, PermissionReplies.Reject, message, CancellationToken.None).ConfigureAwait(false);
                if (reply.IsFailure)
                    LogRejectRefused(ask.Id, reply.Error.Description);
            }
        }
        catch (Exception ex)
        {
            LogRejectFailed(ask.Id, ex);
        }
    }

    private string? Read(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } o
            || !o.TryGetProperty("decision", out var decision)
            || decision.ValueKind != JsonValueKind.String
            || decision.GetString() != "deny")
        {
            return null;
        }

        if (!o.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()))
        {
            LogNoReason();
            return null;
        }

        var text = reason.GetString()!.Trim();
        if (text.Length > MaxReasonLength)
            text = text[..MaxReasonLength];
        var mod = o.TryGetProperty("mod", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } n ? n : "A mod";
        return $"{mod}: {text}";
    }

    private static JsonElement Event(string sessionId, PermissionAsk ask)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("requestId", ask.Id);
            writer.WriteString("kind", ask.Kind);
            writer.WriteString("tool", ask.Tool);
            writer.WriteString("title", ask.Title ?? string.Empty);
            writer.WriteStartArray("patterns");
            foreach (var pattern in ask.Always)
                writer.WriteStringValue(pattern);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A permission.ask hook took longer than {Seconds} s; the ask goes to the user.")]
    private partial void LogTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "permission.ask could not be dispatched; the ask goes to the user.")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "permission.ask denied without a reason; the ask goes to the user.")]
    private partial void LogNoReason();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A mod refused ask {RequestId} but the harness would not take the answer: {Reason}")]
    private partial void LogRejectRefused(string requestId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A mod refused ask {RequestId} but the answer could not be sent.")]
    private partial void LogRejectFailed(string requestId, Exception ex);
}
