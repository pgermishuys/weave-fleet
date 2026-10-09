extern alias FakeLlm;

using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// <c>@</c> a session: the message goes to V2 with Fleet's block naming the session, and the agent reads that session's
/// conversation with <c>fleet_session_read</c> through Fleet's plugin. This fails if the plugin stops offering the tool,
/// the bridge stops finding the caller, or the page stops carrying the other session's messages.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task The_agent_reads_a_session_the_user_referenced_with_fleet_session_read()
    {
        const string earlier = "How does t3code map subagents to threads? (session-read: earlier)";
        const string asked = "Use the subagent mapping from @t3code-notes (session-read: asked)";
        using var cts = new CancellationTokenSource(Timeout);

        // The session to reference, with a conversation of its own.
        fleet.Answer(request => LlmRequest.Starts(request, earlier) ? new ScriptedLlmResponse { Text = "Each subagent is a child thread." } : null);
        var referenced = await fleet.CreateSessionAsync(fleet.NewFolder("session-read-notes"), "t3code notes", cts.Token);
        var referencedEvents = fleet.Watch(cts.Token, referenced);
        await PromptAsync(referenced, earlier, options: null, cts.Token);
        await WaitForAsync(referencedEvents, () => referencedEvents.For(referenced).Any(e => e.Type == "session.idle"), cts.Token);

        string? sessionIdInBlock = null;
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, asked))
            {
                sessionIdInBlock = System.Text.RegularExpressions.Regex.Match(LlmRequest.LastUserText(request) ?? "", "<session ref=\"@t3code-notes\" id=\"([^\"]+)\"").Groups[1].Value;
                return new ScriptedLlmResponse
                {
                    StopReason = "tool_calls",
                    ToolCalls = [new ScriptedToolCall("call_read", "fleet_session_read", JsonSerializer.Serialize(new { sessionId = sessionIdInBlock }))],
                };
            }

            return LlmRequest.Continues(request, asked) ? new ScriptedLlmResponse { Text = "Read it." } : null;
        });

        var id = await fleet.CreateSessionAsync(fleet.NewFolder("session-read"), "Capture subagents", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        string text;
        using (var scope = fleet.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner))
        {
            var expanded = await scope.ServiceProvider.GetRequiredService<SessionReferenceExpander>()
                .ExpandAsync(id, asked, [new SessionReference("@t3code-notes", referenced)], cts.Token);
            text = expanded.Value;
        }

        await PromptAsync(id, text, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);

        // The block named the session as a link (OpenCode 2 has Fleet's tools), and the tool read its conversation.
        sessionIdInBlock.ShouldBe(referenced);
        text.ShouldContain(SessionReferences.LinkNote);
        var requests = fleet.Llm.Queue.Requests.Where(r => LlmRequest.Starts(r, asked) || LlmRequest.Continues(r, asked)).ToList();
        LlmRequest.OfferedToolNames(requests[0]).ShouldContain("fleet_session_read");
        var read = LlmRequest.LastToolText(requests[^1]).ShouldNotBeNull();
        read.ShouldStartWith($"Session \"t3code notes\" ({referenced}) · OpenCode 2 · ");
        read.ShouldContain(earlier);
        read.ShouldContain("Each subagent is a child thread.");
        read.ShouldContain("That's the start of the session.");
    }
}
