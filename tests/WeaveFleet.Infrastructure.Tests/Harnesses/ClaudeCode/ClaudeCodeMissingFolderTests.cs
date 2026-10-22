using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>A prompt to a Claude Code session whose working folder is gone says the folder is gone, not that claude is missing.</summary>
public sealed class ClaudeCodeMissingFolderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fleet-cc-gone-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_prompt_after_the_working_folder_was_deleted_names_the_folder()
    {
        if (OperatingSystem.IsWindows())
            return;

        var claude = Path.Combine(_directory, "claude");
        File.WriteAllText(claude, "#!/bin/sh\ncat > /dev/null\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var work = Path.Combine(_directory, "work");
        Directory.CreateDirectory(work);
        var session = Session(claude, work);
        await using var _ = session;
        Directory.Delete(work);

        var ex = await Should.ThrowAsync<Exception>(() => session.SendPromptAsync("hi", null, CancellationToken.None));

        Console.WriteLine($"TYPE={ex.GetType().Name} MESSAGE={ex.Message}");
        ex.Message.ShouldContain(work);
        ex.Message.ToLowerInvariant().ShouldNotContain("not found");
    }

    [Fact]
    public async Task A_prompt_when_the_claude_binary_is_missing_says_claude_is_missing()
    {
        var work = Path.Combine(_directory, "work");
        Directory.CreateDirectory(work);
        var missing = Path.Combine(_directory, "no-such-claude");
        var session = Session(missing, work);
        await using var _ = session;

        var ex = await Should.ThrowAsync<Exception>(() => session.SendPromptAsync("hi", null, CancellationToken.None));

        Console.WriteLine($"TYPE={ex.GetType().Name} MESSAGE={ex.Message}");
        ex.Message.ShouldNotContain("No process is associated");
        ex.Message.ShouldContain(missing);
    }

    private static ClaudeCodeHarnessSession Session(string binary, string work)
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
            workingDirectory: work,
            config: new ClaudeCodeOptions { BinaryPath = binary },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(2),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId);
    }
}
