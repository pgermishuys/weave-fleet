using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A shell command the user runs from the composer goes to the session's harness only when a prompt could: the
/// session isn't archived and its harness runs shell commands. (That it's the caller's session is the session
/// repository's to check, as it is for a prompt.)
/// </summary>
public sealed class SessionOrchestratorShellCommandTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harnessSession = new("inst-1");

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    private void Seed(string harnessType = "opencode", string retentionStatus = "active", string? agent = null)
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = retentionStatus,
            HarnessType = harnessType,
            UserId = "user-1",
            SelectedAgent = agent,
        });
        _builder.InstanceTracker.Register("inst-1", _harnessSession);
    }

    private void RegisterHarness(string type, bool supportsShellCommands)
        => _builder.RegisterHarness(type, type == "opencode" ? "OpenCode" : "Claude Code", new HarnessCapabilities { SupportsShellCommands = supportsShellCommands });

    [Fact]
    public async Task runs_the_command_through_the_harness_with_the_sessions_agent()
    {
        RegisterHarness("opencode", supportsShellCommands: true);
        Seed(agent: "reviewer");

        var result = await _builder.Build().RunShellCommandAsync("s1", "git status");

        result.IsSuccess.ShouldBeTrue();
        var call = _harnessSession.ShellCommandCalls.ShouldHaveSingleItem();
        call.Command.ShouldBe("git status");
        call.Agent.ShouldBe("reviewer");
        call.MessageId.ShouldNotBeNull().ShouldStartWith("msg_");
    }

    [Fact]
    public async Task refuses_an_archived_session()
    {
        RegisterHarness("opencode", supportsShellCommands: true);
        Seed(retentionStatus: "archived");

        var result = await _builder.Build().RunShellCommandAsync("s1", "git status");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        _harnessSession.ShellCommandCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task refuses_a_harness_that_cant_run_shell_commands()
    {
        RegisterHarness("claude-code", supportsShellCommands: false);
        Seed(harnessType: "claude-code");

        var result = await _builder.Build().RunShellCommandAsync("s1", "ls");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Claude Code sessions can't run shell commands.");
        _harnessSession.ShellCommandCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task refuses_an_empty_command(string? command)
    {
        RegisterHarness("opencode", supportsShellCommands: true);
        Seed();

        var result = await _builder.Build().RunShellCommandAsync("s1", command);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.ShellCommand");
        _harnessSession.ShellCommandCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_so_when_the_harness_wont_run_one_during_a_turn()
    {
        RegisterHarness("opencode", supportsShellCommands: true);
        Seed();
        _harnessSession.RunShellCommandBehavior = (_, _) => throw new HarnessBusyException("The agent is working. Run the command when its turn ends.");

        var result = await _builder.Build().RunShellCommandAsync("s1", "git status");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe("The agent is working. Run the command when its turn ends.");
    }
}
