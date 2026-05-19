using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode does not support the question tool.
/// <see cref="IHarnessSession.AnswerQuestionAsync"/> and <see cref="IHarnessSession.RejectQuestionAsync"/>
/// throw <see cref="NotSupportedException"/> instead of completing normally.
/// These tests document the gap and are expected to FAIL until the feature is implemented.
/// </summary>
[Trait("Gap", "question-tool")]
public sealed class QuestionToolGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-question-{Guid.NewGuid():N}");
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
    public async Task AnswerQuestionAsync_DoesNotThrow()
    {
        // GAP: NuCode throws NotSupportedException — this test is expected to FAIL.
        var exception = await Record.ExceptionAsync(() =>
            _session.AnswerQuestionAsync("fake-request-id", [["Yes"]], CancellationToken.None));

        exception.ShouldBeNull();
    }

    [Fact]
    public async Task RejectQuestionAsync_DoesNotThrow()
    {
        // GAP: NuCode throws NotSupportedException — this test is expected to FAIL.
        var exception = await Record.ExceptionAsync(() =>
            _session.RejectQuestionAsync("fake-request-id", CancellationToken.None));

        exception.ShouldBeNull();
    }
}
