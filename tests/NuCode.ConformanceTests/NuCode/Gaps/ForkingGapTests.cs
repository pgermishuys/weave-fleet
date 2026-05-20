using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Forking is handled at the Fleet orchestrator level (<c>SessionOrchestrator.ForkSessionAsync</c>),
/// not at the harness level. <see cref="IHarnessSession"/> does not expose a fork method.
/// OpenCode has a native <c>POST /session/{id}/fork</c> endpoint, but the Fleet layer creates
/// a new session that spawns a fresh harness instance — NuCode's <c>CreateChildSessionAsync</c>
/// could support message history copying in the future.
///
/// This test validates that NuCode can at least create independent sessions on the same
/// working directory (the building block for fork support at the orchestrator level).
/// </summary>
public sealed class ForkingTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"fork-{Guid.NewGuid():N}");
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
    public async Task IndependentSession_CanBeCreatedFromSameWorkingDirectory()
    {
        // A fork at the Fleet level creates a new session in the same workspace.
        // Verify that two independent NuCode sessions can coexist on the same directory.
        await using var secondFixture = new NuCodeFixture();
        var secondSession = await secondFixture.CreateSessionAsync(_workDir);

        _session.InstanceId.ShouldNotBe(secondSession.InstanceId);
        _session.Status.ShouldBe(HarnessSessionStatus.Idle);
        secondSession.Status.ShouldBe(HarnessSessionStatus.Idle);

        await secondSession.DisposeAsync();
    }
}
