using FakeLlmServer;

namespace WeaveFleet.ConformanceTests.Abstractions;

/// <summary>
/// Abstraction over a harness session under test.
/// Each harness fixture implements this so the same conformance tests
/// can run against every harness.
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
