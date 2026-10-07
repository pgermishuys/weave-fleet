using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeProcessManagerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-claude-args-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Memory_notes_go_to_claude_as_an_appended_system_prompt()
    {
        if (OperatingSystem.IsWindows())
            return;

        var args = await RunAsync("# Fleet memory\n\n## This machine\n- [1a2b3c4d] Use gh for GitHub. (27 Sep 2026)\n");

        var at = args.IndexOf("--append-system-prompt");
        at.ShouldBeGreaterThanOrEqualTo(0);
        args[at + 1].ShouldBe("# Fleet memory");
        args.ShouldContain("- [1a2b3c4d] Use gh for GitHub. (27 Sep 2026)");
    }

    [Fact]
    public async Task Without_notes_there_is_no_appended_system_prompt()
    {
        if (OperatingSystem.IsWindows())
            return;

        (await RunAsync(null)).ShouldNotContain("--append-system-prompt");
    }

    [Fact]
    public async Task Fleets_built_in_skills_load_from_an_added_folder_and_only_when_there_are_some()
    {
        if (OperatingSystem.IsWindows())
            return;

        var args = await RunAsync(null, skillsDirectory: "/fleet/claude-code/skills/0123456789abcdef");

        args[args.IndexOf("--add-dir") + 1].ShouldBe("/fleet/claude-code/skills/0123456789abcdef");
        (await RunAsync(null)).ShouldNotContain("--add-dir");
    }

    /// <summary>Runs a stand-in claude that writes each argument it got on a line of its own, and returns them.</summary>
    private async Task<List<string>> RunAsync(string? appendSystemPrompt, string? skillsDirectory = null)
    {
        var output = Path.Combine(_folder, "args.txt");
        File.Delete(output);
        var claude = Path.Combine(_folder, "claude");
        await File.WriteAllTextAsync(claude, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{output}.tmp' && mv '{output}.tmp' '{output}'\ncat > /dev/null\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var manager = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance);
        await manager.StartAsync(new ClaudeCodeProcessOptions
        {
            BinaryPath = claude,
            WorkingDirectory = _folder,
            PermissionMode = "bypassPermissions",
            AppendSystemPrompt = appendSystemPrompt,
            SkillsDirectory = skillsDirectory,
        }, CancellationToken.None);

        // claude keeps running for the next prompt; the arguments are written as it starts.
        for (var i = 0; i < 50 && !File.Exists(output); i++)
            await Task.Delay(100);
        return [.. (await File.ReadAllTextAsync(output)).Split('\n')];
    }
}
