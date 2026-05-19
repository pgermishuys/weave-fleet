using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Tests that NuCode supports slash commands via <see cref="IHarnessSession.GetCommandsAsync"/>
/// and <see cref="IHarnessSession.SendCommandAsync"/>. Previously a gap — NuCode returned
/// an empty command list and SendCommandAsync was a no-op.
/// </summary>
public sealed class CommandsTests : IAsyncLifetime
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
        commands.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task SendCommandAsync_ExecutesCommand()
    {
        var options = new CommandOptions { Command = "/help" };

        await _session.SendCommandAsync(options, CancellationToken.None);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        page.Messages.ShouldNotBeEmpty();
    }
}
