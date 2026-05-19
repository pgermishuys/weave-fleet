using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode does not support session forking.
/// OpenCode supports forking a session to create an independent copy that can diverge.
/// NuCode has no equivalent capability — <see cref="IHarnessSession"/> does not expose a fork method.
/// These tests document the gap and are expected to FAIL until the feature is implemented.
/// </summary>
[Trait("Gap", "forking")]
public sealed class ForkingGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _fixture = new NuCodeFixture();
        _session = await _fixture.CreateSessionAsync(_workDir);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _session.DisposeAsync();
        await _fixture.DisposeAsync();
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void ForkSession_IsNotSupported()
    {
        // GAP: IHarnessSession does not expose a ForkSessionAsync method.
        // NuCode has no session forking capability.
        // This test is expected to FAIL — it documents the missing feature.
        //
        // When implemented, forking should:
        //   1. Create a new session with the same message history up to the fork point
        //   2. Allow the forked session to diverge independently
        //   3. Return a new IHarnessSession with a distinct InstanceId

        // Fail explicitly to document the gap
        true.ShouldBeFalse("Session forking is not supported by NuCode. IHarnessSession.ForkSessionAsync does not exist.");
    }
}
