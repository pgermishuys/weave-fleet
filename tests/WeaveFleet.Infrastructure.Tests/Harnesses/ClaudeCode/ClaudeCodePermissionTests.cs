using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Claude Code's asks: <c>--permission-prompt-tool stdio</c> sends a <c>can_use_tool</c> control request, Fleet shows it
/// as a <c>permission.asked</c>, and the answer goes back on stdin as a <c>control_response</c>.
/// </summary>
public sealed class ClaudeCodePermissionTests : IDisposable
{
    // As recorded from claude 2.1.283 with --permission-prompt-tool stdio.
    private const string RecordedAsk = """
        {"type":"control_request","request_id":"082ccdda","request":{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hi > hello.txt","description":"Create hello.txt"},"description":"Create hello.txt","permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo hi *"}],"behavior":"allow","destination":"localSettings"},{"type":"addDirectories","directories":["/work"],"destination":"session"}],"blocked_path":"/work/hello.txt","tool_use_id":"toolu_01G5"}}
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("fleet-cc-permission-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_recorded_ask_reads_as_a_shell_ask_with_claudes_suggested_rule()
    {
        var request = JsonSerializer.Deserialize(RecordedAsk, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage)
            .ShouldBeOfType<ClaudeCodeControlRequest>();

        var ask = ClaudeCodePermissions.ToAsk(request.RequestId!, "fleet-1", request.Request!);

        ask.Id.ShouldBe("082ccdda");
        ask.Kind.ShouldBe(PermissionKinds.Shell);
        ask.Tool.ShouldBe("Bash");
        ask.Title.ShouldBe("echo hi > hello.txt");
        ask.Always.ShouldBe(["echo hi *"]);
        ask.CallId.ShouldBe("toolu_01G5");
    }

    [Fact]
    public void An_edit_ask_shows_what_changes()
    {
        var request = new ClaudeCodeControlRequestBody
        {
            Subtype = "can_use_tool",
            ToolName = "Edit",
            Input = JsonDocument.Parse("""{"file_path":"/work/a.ts","old_string":"let a = 1;","new_string":"const a = 1;"}""").RootElement,
        };

        var ask = ClaudeCodePermissions.ToAsk("r1", "fleet-1", request, "/work");

        ask.Kind.ShouldBe(PermissionKinds.Edit);
        ask.Title.ShouldBe("a.ts");
        ClaudeCodePermissions.ToAsk("r1", "fleet-1", request, "/elsewhere").Title.ShouldBe("/work/a.ts");
        ask.Detail.ShouldBe("- let a = 1;\n+ const a = 1;");
    }

    [Fact]
    public void Answers_are_control_responses_naming_the_request()
    {
        var input = JsonDocument.Parse("""{"command":"ls"}""").RootElement;

        var allow = JsonDocument.Parse(ClaudeCodeInput.Allow("r1", input)).RootElement;
        allow.GetProperty("type").GetString().ShouldBe("control_response");
        allow.GetProperty("response").GetProperty("request_id").GetString().ShouldBe("r1");
        allow.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString().ShouldBe("allow");
        allow.GetProperty("response").GetProperty("response").GetProperty("updatedInput").GetProperty("command").GetString().ShouldBe("ls");

        var deny = JsonDocument.Parse(ClaudeCodeInput.Deny("r2", "No")).RootElement.GetProperty("response").GetProperty("response");
        deny.GetProperty("behavior").GetString().ShouldBe("deny");
        deny.GetProperty("message").GetString().ShouldBe("No");

        var prompt = JsonDocument.Parse(ClaudeCodeInput.UserMessage("hello")).RootElement;
        prompt.GetProperty("type").GetString().ShouldBe("user");
        prompt.GetProperty("message").GetProperty("content").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task A_session_that_asks_runs_claude_with_the_prompt_tool_waits_for_the_user_and_answers_on_stdin()
    {
        if (OperatingSystem.IsWindows())
            return;

        var answers = Path.Combine(_directory, "answers.jsonl");
        var arguments = Path.Combine(_directory, "arguments.txt");
        var session = Session(FakeClaude(answers, arguments));
        await using var _ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);

        await session.SendPromptAsync("Push it", null, CancellationToken.None);
        var asked = await NextAsync(session, EventTypes.PermissionAsked);
        asked.Payload!.Value.GetProperty("title").GetString().ShouldBe("git push origin main");

        await session.ReplyToPermissionAsync("req_1", PermissionReplies.Reject, "Not yet", CancellationToken.None);
        await NextAsync(session, EventTypes.SessionIdle);

        var args = await File.ReadAllLinesAsync(arguments);
        args.ShouldContain("--permission-prompt-tool");
        args.ShouldContain("stdio");
        args.SkipWhile(a => a != "--permission-mode").Skip(1).First().ShouldBe("default");
        var lines = await File.ReadAllLinesAsync(answers);
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("Push it");
        var denial = JsonDocument.Parse(lines[1]).RootElement.GetProperty("response").GetProperty("response");
        denial.GetProperty("behavior").GetString().ShouldBe("deny");
        denial.GetProperty("message").GetString()!.ShouldContain("Not yet");
    }

    [Fact]
    public async Task A_session_at_allow_everything_runs_claude_as_before()
    {
        if (OperatingSystem.IsWindows())
            return;

        var answers = Path.Combine(_directory, "answers.jsonl");
        var arguments = Path.Combine(_directory, "arguments.txt");
        var session = Session(FakeClaude(answers, arguments, asks: false));
        await using var _ = session;

        await session.SendPromptAsync("Push it", null, CancellationToken.None);
        await NextAsync(session, EventTypes.SessionIdle);

        var args = await File.ReadAllLinesAsync(arguments);
        args.ShouldNotContain("--permission-prompt-tool");
        args.SkipWhile(a => a != "--permission-mode").Skip(1).First().ShouldBe("bypassPermissions");
        (await File.ReadAllTextAsync(answers)).ShouldBe("Push it");
    }

    /// <summary>
    /// A stand-in for <c>claude</c>: writes its arguments and every stdin line down, asks once when <paramref name="asks"/>,
    /// and ends the turn after the answer.
    /// </summary>
    private string FakeClaude(string answers, string arguments, bool asks = true)
    {
        var claude = Path.Combine(_directory, "claude");
        var script = asks
            ? $$$"""
                #!/bin/sh
                printf '%s\n' "$@" > '{{{arguments}}}'
                IFS= read -r prompt
                printf '%s\n' "$prompt" > '{{{answers}}}'
                echo '{"type":"system","subtype":"init","session_id":"cc-1"}'
                echo '{"type":"control_request","request_id":"req_1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"git push origin main"},"tool_use_id":"toolu_1"}}'
                IFS= read -r answer
                printf '%s\n' "$answer" >> '{{{answers}}}'
                echo '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"cc-1"}'
                cat > /dev/null
                """
            : $$$"""
                #!/bin/sh
                printf '%s\n' "$@" > '{{{arguments}}}'
                cat > '{{{answers}}}'
                echo '{"type":"system","subtype":"init","session_id":"cc-1"}'
                echo '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"cc-1"}'
                """;
        File.WriteAllText(claude, script.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return claude;
    }

    private ClaudeCodeHarnessSession Session(string binary)
    {
        var messages = new InMemoryMessageRepository();
        var delegations = new InMemoryDelegationRepository();
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, messages, delegations, sessions, new InMemorySmartLinkRepository(), new InMemoryOutboxRepository(), new FakeOutboxDispatcher()));

        return new ClaudeCodeHarnessSession(
            instanceId: "cc-instance",
            fleetSessionId: "fleet-cc",
            workingDirectory: _directory,
            config: new ClaudeCodeOptions { BinaryPath = binary },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(2),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId);
    }

    private static async Task<HarnessEvent> NextAsync(ClaudeCodeHarnessSession session, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var evt in session.SubscribeAsync(timeout.Token))
        {
            if (evt.Type == type)
                return evt;
        }

        throw new TimeoutException($"No {type} event.");
    }
}
