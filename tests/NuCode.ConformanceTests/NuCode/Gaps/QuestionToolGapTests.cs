using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Tests that <see cref="IHarnessSession.AnswerQuestionAsync"/> and
/// <see cref="IHarnessSession.RejectQuestionAsync"/> complete without throwing.
/// Previously a gap — NuCode threw <see cref="NotSupportedException"/>.
/// Now bridges to <see cref="global::NuCode.Tools.IQuestionService.ReplyToQuestion"/>.
/// </summary>
public sealed class QuestionToolTests : IAsyncLifetime
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
        var exception = await Record.ExceptionAsync(() =>
            _session.AnswerQuestionAsync("fake-request-id", [["Yes"]], CancellationToken.None));

        exception.ShouldBeNull();
    }

    [Fact]
    public async Task RejectQuestionAsync_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() =>
            _session.RejectQuestionAsync("fake-request-id", CancellationToken.None));

        exception.ShouldBeNull();
    }
}
