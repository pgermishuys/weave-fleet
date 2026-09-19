extern alias FakeLlm;

using System.Collections.Concurrent;
using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.IntegrationTests.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// What the OpenCode 2 live tests share: Fleet on Kestrel, one real <c>opencode2</c> server (Fleet runs one per owner)
/// with a scratch HOME whose config points at a scripted model, and that model. The server never reads or writes the
/// real user's OpenCode data, and Fleet has its own database. Each test starts sessions of its own, in folders of its
/// own, and the model answers each by what it's asked (<see cref="Answer"/>), not by arrival order.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xunit disposes a class fixture through IAsyncLifetime.")]
public sealed class OpenCode2LiveFleet : IAsyncLifetime
{
    public const string Owner = "local-user";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-oc2-live-{Guid.NewGuid():N}");
    private readonly ConcurrentQueue<Func<string, ScriptedLlmResponse?>> _answers = new();
    private PooledOpenCodeLiveHost.KestrelFleetFactory? _factory;

    public FakeLlmServerFixture Llm { get; private set; } = null!;

    public IServiceProvider Services => _factory?.LiveServices
        ?? throw new InvalidOperationException("opencode2 isn't installed, so no Fleet was started.");

    public OpenCode2HarnessRuntime Runtime => Services.GetRequiredService<OpenCode2HarnessRuntime>();

    private static readonly Lazy<bool> Installed = new(ProbeInstalled);

    /// <summary>Whether a 2.x <c>opencode2</c> is installed where the harness looks for it (<see cref="FindExecutable"/>).</summary>
    public static bool IsInstalled() => Installed.Value;

    /// <summary>
    /// The <c>opencode2</c> the harness would start, found the way it finds it (<see cref="OpenCode2Install.Locate"/>:
    /// the remembered mode's install, else a separate one under <c>~/.weave/harnesses/opencode2</c>, else PATH and
    /// <c>~/.opencode/bin</c>). Reads only; it doesn't remember a mode.
    /// </summary>
    internal static string? FindExecutable()
        => new OpenCode2Install(
                ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable,
                ExecutableResolver.UserBinDirectories(),
                (path, ct) => HarnessProbe.CheckInstalledAsync("OpenCode 2", OpenCode2Executable.Command, path, NullLogger.Instance, ct),
                OperatingSystem.IsWindows())
            .Locate()?.ExecutablePath;

    private static bool ProbeInstalled()
    {
        if (FindExecutable() is not { } executable)
            return false;

        try
        {
            var probe = HarnessProbe.RunAsync(executable, ["--version"], CancellationToken.None).GetAwaiter().GetResult();
            return HarnessProbe.ParseVersion(probe.StandardOutput) is { } version && OpenCode2Executable.IsOpenCode2(version);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Starts nothing when <c>opencode2</c> isn't installed; the tests skip, or fail in CI.</remarks>
    public async Task InitializeAsync()
    {
        if (!IsInstalled())
            return;

        Directory.CreateDirectory(_root);
        Llm = await FakeLlmServerFixture.StartAsync();

        // Title generation offers no tools and comes at V2's own timing: it never uses up an answer.
        Llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Live test" };
        Llm.Queue.Fallback = request =>
        {
            foreach (var answer in _answers)
            {
                if (answer(request) is { } response)
                    return response;
            }

            return new ScriptedLlmResponse { Text = "Done." };
        };

        var dbPath = Path.Combine(_root, "fleet", "fleet.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try { _ = _factory.Services; }
        catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

        Runtime.ServerEnvironment = WriteScratchHome(Path.Combine(_root, "opencode2"), Llm.BaseUrl);

        // Sessions start only in folders under a workspace root, as in the new-session composer.
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        using var user = BackgroundUserContext.BeginScope(Owner);
        using var scope = Services.CreateScope();
        var root = await scope.ServiceProvider.GetRequiredService<WorkspaceRootService>().AddRootAsync(Path.Combine(_root, "work"));
        root.IsSuccess.ShouldBeTrue(root.IsFailure ? root.Error.Description : null);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (Llm is not null)
            await Llm.DisposeAsync();

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Adds how the model answers a request, e.g. a test's prompt; <see langword="null"/> leaves it to the others.</summary>
    public void Answer(Func<string, ScriptedLlmResponse?> answer) => _answers.Enqueue(answer);

    /// <summary>A new folder for a test's sessions.</summary>
    public string NewFolder(string name)
    {
        var folder = Path.Combine(_root, "work", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Creates an OpenCode 2 session in <paramref name="folder"/> the way the new-session composer does.</summary>
    public async Task<string> CreateSessionAsync(string folder, string title, CancellationToken ct)
    {
        var created = await WithOrchestratorAsync(orchestrator => orchestrator.CreateSessionAsync(
            new CreateSessionRequest { Directory = folder, Title = title, HarnessType = OpenCode2HarnessSession.Type }, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value.Session.Id;
    }

    /// <summary>Runs <paramref name="call"/> on the orchestrator as the owner, as a request of theirs would.</summary>
    public async Task<T> WithOrchestratorAsync<T>(Func<SessionOrchestrator, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(Owner);
        using var scope = Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<SessionOrchestrator>());
    }

    /// <summary>The harness session Fleet runs for <paramref name="sessionId"/>, starting it if it isn't running.</summary>
    public async Task<IHarnessSession> HarnessSessionAsync(string sessionId, CancellationToken ct)
    {
        var active = await WithOrchestratorAsync(orchestrator => orchestrator.ActivateSessionAsync(sessionId, ct));
        active.IsSuccess.ShouldBeTrue(active.IsFailure ? active.Error.Description : null);
        return active.Value;
    }

    /// <summary>Collects what Fleet broadcasts to the owner about <paramref name="sessionIds"/> until <paramref name="ct"/> ends.</summary>
    public LiveEvents Watch(CancellationToken ct, params string[] sessionIds)
    {
        var events = new LiveEvents();
        var broadcaster = Services.GetRequiredService<IEventBroadcaster>();
        events.Collecting = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in broadcaster.SubscribeAsync([.. sessionIds.Select(id => $"session:{id}")], Owner, ct))
                    events.Add(e);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        return events;
    }

    /// <summary>
    /// A user config for V2 that uses the fake model, with a second model and a second agent to switch to. Returns the
    /// variables that point a server at it.
    /// </summary>
    private static Dictionary<string, string> WriteScratchHome(string root, Uri llmBaseUrl)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var folder in environment.Values)
            Directory.CreateDirectory(folder);

        // A separate install (next to OpenCode 1, as in CI) gets its own config folder and database from Fleet: these
        // point it at the scratch ones instead.
        var configFolder = Path.Combine(environment["XDG_CONFIG_HOME"], "opencode");
        Directory.CreateDirectory(configFolder);
        environment["OPENCODE_CONFIG_DIR"] = configFolder;
        environment["OPENCODE_DB"] = Path.Combine(environment["XDG_DATA_HOME"], "opencode", "opencode.db");
        Directory.CreateDirectory(Path.GetDirectoryName(environment["OPENCODE_DB"])!);
        File.WriteAllText(Path.Combine(configFolder, "opencode.json"), $$"""
            {
              "model": "fake/fake-model",
              "small_model": "fake/fake-model",
              "agent": {
                "reviewer": { "description": "Reviews code", "mode": "primary", "prompt": "You review code. {{ReviewerMarker}}" }
              },
              "provider": {
                "fake": {
                  "npm": "@ai-sdk/openai-compatible",
                  "options": { "baseURL": "{{llmBaseUrl.ToString().TrimEnd('/')}}/v1", "apiKey": "fake-key" },
                  "models": {
                    "fake-model": { "name": "Fake", "tool_call": true },
                    "fake-model-2": { "name": "Fake Two", "tool_call": true }
                  }
                }
              }
            }
            """);
        return environment;
    }

    /// <summary>In the reviewer agent's prompt, so a request shows which agent it's for.</summary>
    public const string ReviewerMarker = "AGENT-REVIEWER";
}

/// <summary>What Fleet broadcast about a test's sessions, in order.</summary>
public sealed class LiveEvents
{
    private readonly ConcurrentQueue<BroadcastEvent> _events = new();

    internal Task Collecting { get; set; } = Task.CompletedTask;

    public IReadOnlyList<BroadcastEvent> All => [.. _events];

    internal void Add(BroadcastEvent e) => _events.Enqueue(e);

    public IEnumerable<BroadcastEvent> For(string sessionId) => All.Where(e => e.Topic == $"session:{sessionId}");

    public string Describe() => string.Join(", ", All.Select(e => $"{e.Topic.Replace("session:", "", StringComparison.Ordinal)}:{e.Type}"));
}

/// <summary>Reads the model's side of a conversation from an OpenAI chat request body.</summary>
internal static class LlmRequest
{
    public static bool OffersTools(string request)
    {
        using var body = JsonDocument.Parse(request);
        return body.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
    }

    public static string? Model(string request)
    {
        using var body = JsonDocument.Parse(request);
        return body.RootElement.TryGetProperty("model", out var model) ? model.GetString() : null;
    }

    public static string? LastRole(string request)
    {
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("role").GetString();
    }

    public static string? FirstUserText(string request) => Texts(request, "user").FirstOrDefault();

    public static string System(string request) => string.Join("\n", Texts(request, "system"));

    /// <summary>The latest tool result, when the request answers a tool call.</summary>
    public static string? LastToolText(string request)
    {
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        return last.GetProperty("role").GetString() == "tool" ? ContentText(last.GetProperty("content")) : null;
    }

    /// <summary>Whether the request is a turn's first model call, and the turn's prompt has <paramref name="prompt"/>.</summary>
    public static bool Starts(string request, string prompt) => OffersTools(request) && LastRole(request) == "user" && Asked(request, prompt);

    /// <summary>Whether the request answers a tool call in a turn whose prompt has <paramref name="prompt"/>.</summary>
    public static bool Continues(string request, string prompt) => LastRole(request) == "tool" && Asked(request, prompt);

    /// <summary>
    /// The latest user message, the turn's prompt, has <paramref name="prompt"/>; earlier turns' prompts are history. V2
    /// gives a subagent its prompt after instructions of its own, so it's a part of the message, not all of it.
    /// </summary>
    private static bool Asked(string request, string prompt)
        => Texts(request, "user").LastOrDefault() is { } text && text.Contains(prompt, StringComparison.Ordinal);

    public static HashSet<string> OfferedToolNames(string request)
    {
        using var body = JsonDocument.Parse(request);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (body.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("function", out var function) && function.TryGetProperty("name", out var name))
                    names.Add(name.GetString()!);
            }
        }

        return names;
    }

    private static List<string> Texts(string request, string role)
    {
        using var body = JsonDocument.Parse(request);
        return body.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == role)
            .Select(m => ContentText(m.GetProperty("content")))
            .ToList();
    }

    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString()!,
        JsonValueKind.Array => string.Concat(content.EnumerateArray()
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)),
        _ => string.Empty,
    };
}

/// <summary>
/// A test that needs a real OpenCode 2 (<c>opencode2</c>, 2.x), where the harness finds it. Skipped when it isn't installed, unless
/// <c>FLEET_REQUIRE_OPENCODE2=1</c> (CI), which fails it instead.
/// </summary>
internal sealed class OpenCode2FactAttribute : FactAttribute
{
    public OpenCode2FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLEET_REQUIRE_OPENCODE2") != "1" && !OpenCode2LiveFleet.IsInstalled())
            Skip = "OpenCode 2 (opencode2, 2.x) isn't installed where the harness looks: ~/.weave/harnesses/opencode2, PATH or ~/.opencode/bin.";
    }
}
