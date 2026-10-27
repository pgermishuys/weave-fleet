extern alias FakeLlm;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// What the OpenCode 2 live test classes share: the wait and prompt helpers and the readers of Fleet's live events.
/// Each class gets its own <see cref="OpenCode2LiveFleet"/>, so xUnit runs the classes side by side.
/// </summary>
public abstract partial class OpenCode2LiveTestBase
{
    private protected static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    protected abstract OpenCode2LiveFleet Fleet { get; }

    // Fleet forgets a harness process from Process.Exited, which .NET raises on a thread-pool thread after the
    // process is gone, so the registry can still list it for a moment after the OS says it has exited.
    private protected static async Task<bool> IsForgottenAsync(Func<int, bool> pids, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessGroupHelper.RunningProcesses.Any(p => pids(p.Pid)))
                return true;
            await Task.Delay(50);
        }

        return !ProcessGroupHelper.RunningProcesses.Any(p => pids(p.Pid));
    }

    private protected static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private protected async Task PromptAsync(string sessionId, string text, PromptOptions? options, CancellationToken ct)
    {
        var prompted = await Fleet.WithOrchestratorAsync(o => o.PromptSessionAsync(sessionId, text, options, ct));
        prompted.IsSuccess.ShouldBeTrue(prompted.IsFailure ? prompted.Error.Description : null);
    }

    private protected async Task<IReadOnlyList<WeaveFleet.Application.DTOs.DelegationDto>> Delegations(string sessionId)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = Fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DelegationService>().GetDelegationsAsync(sessionId);
    }

    private protected static ScriptedLlmResponse ToolCall(string callId, string tool, object input) => new()
    {
        StopReason = "tool_calls",
        ToolCalls = [new ScriptedToolCall(callId, tool, JsonSerializer.Serialize(input))],
    };

    private protected static object Question(string question) => new
    {
        questions = new[]
        {
            new
            {
                question,
                header = "Pick",
                options = new[] { new { label = "A", description = "The first" }, new { label = "B", description = "The second" } },
            },
        },
    };

    /// <summary>Fleet says a session is working with <c>session.status</c> <c>busy</c>.</summary>
    private protected static bool IsBusy(BroadcastEvent e)
        => e.Type == "session.status"
            && e.Payload.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("type", out var type)
            && type.GetString() == ActivityStatuses.Busy;

    private protected static IEnumerable<T> Parts<T>(LiveEvents events, string sessionId) where T : MessageEventPart
        => events.For(sessionId).Select(e => e.DomainEvent).OfType<MessagePartUpdated>().Select(e => e.Payload.Part).OfType<T>();

    /// <summary>Each part as the live stream last showed it, in the order they first appeared.</summary>
    private protected static List<T> LatestParts<T>(LiveEvents events, string sessionId) where T : MessageEventPart
        => Parts<T>(events, sessionId).GroupBy(p => p.Id).Select(g => g.Last()).ToList();

    private protected static IEnumerable<MessageLifecyclePayload> Messages(LiveEvents events, string sessionId)
        => events.For(sessionId).Select(e => e.DomainEvent).OfType<MessageUpdated>().Select(e => e.Payload);

    private protected static JsonNode ToJson(string sessionId, MessageEventPart part)
        => JsonNode.Parse(JsonSerializer.Serialize(
            new MessagePartUpdatedPayload { SessionId = sessionId, Part = part },
            InfrastructureJsonContext.Default.MessagePartUpdatedPayload))!["part"]!;

    private protected Task<object> WaitForAsync(LiveEvents events, Func<bool> done, CancellationToken ct)
        => WaitForAsync(events, () => Task.FromResult<object?>(done() ? true : null), ct);

    private protected Task<object> WaitForAsync(LiveEvents events, Func<Task<bool>> done, CancellationToken ct)
        => WaitForAsync(events, async () => await done() ? true : (object?)null, ct);

    /// <summary>Polls until <paramref name="read"/> has a value; on a timeout, says what Fleet sent and what the model was asked.</summary>
    private protected async Task<T> WaitForAsync<T>(LiveEvents events, Func<Task<T?>> read, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await read() is { } value)
                return value;

            try
            {
                await Task.Delay(200, wait.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out. Fleet sent: {events.Describe()}\n" +
                    "The model was asked:\n" + string.Join("\n", Fleet.Llm.Queue.Requests.Select(r =>
                        $"  first={Trim(LlmRequest.FirstUserText(r))} last={LlmRequest.LastRole(r)} tool={Trim(LlmRequest.LastToolText(r))}")));
            }
        }
    }

    private protected static string Trim(string? value) => value is null ? "-" : value[..Math.Min(value.Length, 120)];

    [GeneratedRegex(@"cv_[A-Za-z0-9]+")]
    private protected static partial Regex CanvasId();

    /// <summary>The processes of this user whose environment has <paramref name="name"/>=<paramref name="value"/> (Linux).</summary>
    private protected static List<int> ProcessesWith(string name, string value)
    {
        var wanted = $"{name}={value}";
        var found = new List<int>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out var pid))
                continue;
            try
            {
                if (File.ReadAllText(Path.Combine(folder, "environ")).Split('\0').Contains(wanted))
                    found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or gone.
            }
        }

        return found;
    }

    /// <summary>A profile whose provider (<c>prof</c>) is the scripted model under the model id <paramref name="model"/>, with an agent of its own.</summary>
    private protected string ProfileContent(string model, string extra = "") => $$"""
        {
          // The scripted model again, as a provider of the profile's own.
          "model": "prof/{{model}}",
          "provider": {
            "prof": {
              "npm": "@ai-sdk/openai-compatible",
              "options": { "baseURL": "{{Fleet.Llm.BaseUrl.ToString().TrimEnd('/')}}/v1", "apiKey": "profile-key" },
              "models": { "{{model}}": { "name": "{{model}}", "tool_call": true } }
            }
          },
          "agent": { "profiled": { "description": "Only on the profile", "mode": "primary", "prompt": "You are the profile's agent." } }{{extra}}
        }
        """;

    private protected async Task<HarnessProfileView> CreateProfileAsync(string name, string content, CancellationToken ct)
    {
        var created = await WithProfilesAsync(p => p.CreateAsync(OpenCode2HarnessSession.Type, name, content, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value;
    }

    private protected async Task<string> CreateSessionAsync(string folder, string title, string profileId, CancellationToken ct)
    {
        var created = await Fleet.WithOrchestratorAsync(o => o.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = folder,
            Title = title,
            HarnessType = OpenCode2HarnessSession.Type,
            HarnessProfileId = profileId,
        }, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value.Session.Id;
    }

    private protected async Task<WeaveFleet.Domain.Harnesses.HarnessCatalog> CatalogAsync(string folder, string profileId, CancellationToken ct)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = Fleet.Services.CreateScope();
        var catalog = await scope.ServiceProvider.GetRequiredService<HarnessCatalogService>()
            .GetCatalogAsync(OpenCode2HarnessSession.Type, folder, ct, profileId);
        catalog.IsSuccess.ShouldBeTrue(catalog.IsFailure ? catalog.Error.Description : null);
        return catalog.Value.ShouldNotBeNull();
    }

    private protected async Task<T> WithProfilesAsync<T>(Func<HarnessProfileService, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = Fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<HarnessProfileService>());
    }

    /// <summary>Turns workflows on or off as Settings does; the owner's next session gets a server to match.</summary>
    private protected async Task SetWorkflowsAsync(bool enabled)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = Fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(FleetWorkflows.PreferenceKey, enabled ? "true" : "false");
    }
}
