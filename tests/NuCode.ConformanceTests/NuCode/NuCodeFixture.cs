using FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuCode.ConformanceTests.Fakes;
using WeaveFleet.Infrastructure.Harnesses.NuCode;

namespace NuCode.ConformanceTests.NuCode;

/// <summary>
/// <see cref="IHarnessSessionFixture"/> for <see cref="NuCodeHarnessSession"/>.
/// Builds a real NuCode DI container with a <see cref="ScriptedChatClient"/> as the LLM,
/// so tests can control responses without real API calls.
/// </summary>
public sealed class NuCodeFixture : IHarnessSessionFixture
{
    private readonly ScriptedChatClient _chatClient = new();
    private ServiceProvider? _nuCodeProvider;

    /// <inheritdoc />
    public Task<IHarnessSession> CreateSessionAsync(string workingDirectory, CancellationToken ct = default)
        => CreateSessionAsync(workingDirectory, null, ct);

    /// <summary>Creates a session with an optional logger factory for diagnostics.</summary>
    public Task<IHarnessSession> CreateSessionAsync(string workingDirectory, ILoggerFactory? loggerFactory, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = workingDirectory;
        });

        // Override IQuestionService — same as production harness
        services.AddSingleton<global::NuCode.Tools.IQuestionService, DenyAllQuestionServiceFake>();

        // Register the scripted chat client as the LLM
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(_chatClient);

        // Register a logger factory so internal services (e.g. SessionProcessor) can resolve ILogger<T>
        var effectiveLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        services.AddSingleton<ILoggerFactory>(effectiveLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        _nuCodeProvider = services.BuildServiceProvider();

        var instanceId = $"nucode-test-{Guid.NewGuid():N}";
        var fleetSessionId = $"fleet-test-{Guid.NewGuid():N}";

        var logger = loggerFactory?.CreateLogger<NuCodeHarnessSession>()
            ?? (ILogger<NuCodeHarnessSession>)NullLogger<NuCodeHarnessSession>.Instance;

        var session = new NuCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: fleetSessionId,
            workingDirectory: workingDirectory,
            provider: "fake",
            modelId: "fake-model",
            discoveredModels: [],
            projectId: null,
            projectName: null,
            ownerUserId: "test-user",
            scopeFactory: new NoOpServiceScopeFactory(),
            nuCodeProvider: _nuCodeProvider,
            chatClient: _chatClient,
            logger: logger);

        return Task.FromResult<IHarnessSession>(session);
    }

    /// <inheritdoc />
    public void EnqueueResponse(ScriptedLlmResponse response) => _chatClient.Enqueue(response);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_nuCodeProvider is not null)
        {
            await _nuCodeProvider.DisposeAsync();
        }
    }

    private sealed class DenyAllQuestionServiceFake : global::NuCode.Tools.IQuestionService
    {
        private const string DenialMessage = "Questions are not supported in this context.";

        public Task<string> AskAsync(
            global::NuCode.SessionId sessionId,
            string header,
            string question,
            IReadOnlyList<string> options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DenialMessage);

        public void ReplyToQuestion(string requestId, string answer) { }

        public IReadOnlyList<global::NuCode.Tools.QuestionRequest> GetPendingQuestions() => [];
    }

    /// <summary>
    /// A no-op <see cref="IServiceScopeFactory"/> used to satisfy the NuCodeHarnessSession
    /// constructor. Delegation features (which require DB access) are not exercised in
    /// conformance tests, so this returns an empty scope.
    /// </summary>
    private sealed class NoOpServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NoOpServiceScope();

        private sealed class NoOpServiceScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new NoOpServiceProvider();
            public void Dispose() { }
        }

        private sealed class NoOpServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
