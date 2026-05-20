using FakeLlmServer;

namespace NuCode.ConformanceTests.Abstractions;

/// <summary>
/// Abstraction over a harness session under test.
/// Both NuCode and OpenCode fixtures implement this so the same
/// conformance tests can run against both harnesses.
/// </summary>
public interface IHarnessSessionFixture : IAsyncDisposable
{
    /// <summary>
    /// Create a new harness session rooted at the given working directory.
    /// </summary>
    Task<IHarnessSession> CreateSessionAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>
    /// Enqueue a scripted LLM response. The next prompt sent to the session
    /// will receive this response from the underlying fake LLM.
    /// </summary>
    void EnqueueResponse(ScriptedLlmResponse response);
}
