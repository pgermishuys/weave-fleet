using FakeLlmServer;
using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode does not support slash commands.
/// <see cref="IHarnessSession.GetCommandsAsync"/> always returns an empty list and
/// <see cref="IHarnessSession.SendCommandAsync"/> is a no-op.
/// These tests document the gap and are expected to FAIL until the feature is implemented.
/// </summary>
[Trait("Gap", "commands")]
public sealed class CommandsGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-commands-{Guid.NewGuid():N}");
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
    public async Task GetCommandsAsync_ReturnsAvailableCommands()
    {
        var commands = await _session.GetCommandsAsync(CancellationToken.None);

        // GAP: NuCode always returns an empty list — this test is expected to FAIL.
        commands.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task SendCommandAsync_ExecutesCommand()
    {
        // Arrange: enqueue a response for the command to consume
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Command executed." });

        var options = new CommandOptions { Command = "/help" };

        await _session.SendCommandAsync(options, CancellationToken.None);

        // Assert: the command should have produced a message
        var page = await _session.GetMessagesAsync(null, CancellationToken.None);

        // GAP: NuCode SendCommandAsync is a no-op and produces no messages — this test is expected to FAIL.
        page.Messages.ShouldNotBeEmpty();
    }
}
