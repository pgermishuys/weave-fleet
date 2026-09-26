using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// A call left running by a turn that was cut off (Fleet stopped, the harness died, the machine went away) used to
/// show a working dot forever. Outside a turn nothing can be running, so it reads as failed, except for work that
/// legitimately carries on: a call the harness moved into the background, and a sub-agent still working.
/// </summary>
public sealed class CutOffToolCallsTests
{
    private const string SessionId = "session-1";
    private const string InstanceId = "instance-1";
    private static readonly JsonElement Args = JsonDocument.Parse("""{"command":"sleep 300"}""").RootElement.Clone();

    [Fact]
    public async Task A_call_left_running_after_the_turn_reads_as_cut_off()
    {
        var proxy = LiveProxy(activity: "idle",
            new ToolUsePart("call-running", "bash", Args, ToolUseState.Running),
            new ToolUsePart("call-pending", "bash", Args, ToolUseState.Pending));

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        tools["call-running"].ShouldBeOfType<ToolErrorState>().Error.ShouldBe(CutOffToolCalls.Message);
        tools["call-running"].ShouldBeOfType<ToolErrorState>().Input.ShouldNotBeNull();
        tools["call-pending"].ShouldBeOfType<ToolErrorState>().Error.ShouldBe(CutOffToolCalls.Message);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("retry")]
    [InlineData("waiting_input")]
    public async Task A_call_in_a_turn_keeps_running(string activity)
    {
        var proxy = LiveProxy(activity, new ToolUsePart("call-running", "bash", Args, ToolUseState.Running));

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        tools["call-running"].ShouldBeOfType<ToolRunningState>();
    }

    [Fact]
    public async Task Finished_calls_are_left_as_they_are()
    {
        var proxy = LiveProxy(activity: "idle",
            new ToolUsePart("call-done", "bash", Args, ToolUseState.Completed),
            new ToolUsePart("call-failed", "bash", Args, ToolUseState.Error) { Error = "exit 1" });

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        tools["call-done"].ShouldBeOfType<ToolCompletedState>();
        tools["call-failed"].ShouldBeOfType<ToolErrorState>().Error.ShouldBe("exit 1");
    }

    [Fact]
    public async Task A_call_moved_into_the_background_keeps_running()
    {
        var proxy = LiveProxy(activity: "idle",
            new ToolUsePart("call-background", "bash", Args, ToolUseState.Running) { Background = true });

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        tools["call-background"].ShouldBeOfType<ToolRunningState>().Background.ShouldBeTrue();
    }

    [Theory]
    [InlineData("busy", false, true)]
    [InlineData("waiting_input", false, true)]
    [InlineData("idle", true, true)]
    [InlineData("idle", false, false)]
    public async Task A_sub_agent_call_keeps_running_while_its_child_works(string childActivity, bool childInBackground, bool stillRunning)
    {
        var activity = new SessionActivityTracker();
        activity.Update(SessionId, "idle", "user-1");
        activity.Update("child", childActivity, "user-1");
        activity.RegisterChild("child", SessionId);
        if (childInBackground)
            activity.MoveChildToBackground("child");
        var delegations = new InMemoryDelegationRepository();
        delegations.Seed(new Delegation { Id = "d-1", ParentSessionId = SessionId, ChildSessionId = "child", ParentToolCallId = "call-task" });
        var proxy = LiveProxy(activity, delegations, new ToolUsePart("call-task", "task", Args, ToolUseState.Running));

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        if (stillRunning)
            tools["call-task"].ShouldBeOfType<ToolRunningState>();
        else
            tools["call-task"].ShouldBeOfType<ToolErrorState>();
    }

    [Fact]
    public async Task Messages_read_as_pages_settle_the_same_way()
    {
        var proxy = LiveProxy(activity: "idle", new ToolUsePart("call-running", "bash", Args, ToolUseState.Running));

        var page = await proxy.GetMessagesAsync(SessionId);

        var tool = page.Messages.Single().Parts.OfType<ToolUsePart>().Single();
        tool.State.ShouldBe(ToolUseState.Error);
        tool.Error.ShouldBe(CutOffToolCalls.Message);
    }

    [Fact]
    public async Task History_Fleet_keeps_itself_settles_too()
    {
        // Claude Code's history is Fleet's own record, where the cut-off call's last word was "running".
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = SessionId, HarnessType = "claude-code", Title = "t", Status = "stopped", UserId = "user-1" });
        var activity = new SessionActivityTracker();
        var builder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (id, _, _) => Task.FromResult(new SessionSnapshot
            {
                Session = new SessionSnapshotSession { Id = id, Title = "t", Status = "stopped" },
                Messages =
                [
                    new MessageLifecyclePayload
                    {
                        Info = new MessageEventInfo { Id = "msg-1", SessionId = id, Role = "assistant", Time = new MessageEventTime { Created = 1 } },
                        Parts =
                        [
                            new ToolMessageEventPart
                            {
                                Id = "part-1",
                                SessionId = id,
                                MessageId = "msg-1",
                                ToolName = "Bash",
                                CallId = "toolu_1",
                                State = new ToolRunningState { Input = Args },
                            },
                        ],
                    },
                ],
                ActivityStatus = "idle",
            }),
        };
        var proxy = new OpenCodeSessionMessageProxy(
            sessions, new InstanceTracker(), activity, new InMemoryDelegationRepository(), builder,
            new ServiceCollection().BuildServiceProvider(), Harnesses(), NullLogger<OpenCodeSessionMessageProxy>.Instance);

        var tools = Tools(await proxy.GetSnapshotAsync(SessionId));

        tools["toolu_1"].ShouldBeOfType<ToolErrorState>().Error.ShouldBe(CutOffToolCalls.Message);
    }

    private static Dictionary<string, ToolInvocationState> Tools(SessionSnapshot snapshot)
        => snapshot.Messages.SelectMany(m => m.Parts).OfType<ToolMessageEventPart>().ToDictionary(p => p.CallId, p => p.State);

    private static HarnessRegistry Harnesses()
        => new([new OpenCodeHarness(), new OpenCode2Harness(), new ClaudeCodeHarness()], []);

    private static OpenCodeSessionMessageProxy LiveProxy(string activity, params ToolUsePart[] tools)
    {
        var tracker = new SessionActivityTracker();
        tracker.Update(SessionId, activity, "user-1");
        return LiveProxy(tracker, new InMemoryDelegationRepository(), tools);
    }

    private static OpenCodeSessionMessageProxy LiveProxy(
        SessionActivityTracker activity,
        InMemoryDelegationRepository delegations,
        params ToolUsePart[] tools)
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = SessionId, InstanceId = InstanceId, HarnessType = "opencode", Title = "t", Status = "active", UserId = "user-1" });
        var instances = new InstanceTracker();
        instances.Register(InstanceId, new FakeHarnessSession(InstanceId)
        {
            GetMessagesBehavior = (_, _) => Task.FromResult(new MessagePage(
                [new HarnessMessage { Id = "msg-1", Role = "assistant", Parts = [.. tools], Timestamp = DateTimeOffset.UtcNow }],
                false)),
        });

        return new OpenCodeSessionMessageProxy(
            sessions, instances, activity, delegations, new FakeSessionSnapshotBuilder(),
            new ServiceCollection().BuildServiceProvider(), Harnesses(), NullLogger<OpenCodeSessionMessageProxy>.Instance);
    }
}
