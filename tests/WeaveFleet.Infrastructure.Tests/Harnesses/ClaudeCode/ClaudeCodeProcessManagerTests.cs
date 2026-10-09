using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses;
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

    [Fact]
    public async Task What_the_agent_left_running_outlives_claude_and_ends_with_the_sessions_group()
    {
        // A stand-in claude whose shell starts a server and returns, as an agent's shell tool does, so the server is left
        // with no parent: on Windows from PowerShell's Start-Process, elsewhere from a subshell.
        var pidFile = Path.Combine(_folder, "server.pid");
        string claude;
        if (OperatingSystem.IsWindows())
        {
            claude = Path.Combine(_folder, "claude.cmd");
            await File.WriteAllTextAsync(claude,
                "@echo off\r\n"
                + "powershell -NoProfile -Command \"(Start-Process ping -ArgumentList '-n','300','127.0.0.1' -WindowStyle Hidden -PassThru).Id"
                + $" | Set-Content -Path '{pidFile}.tmp'; Move-Item '{pidFile}.tmp' '{pidFile}'\"\r\n"
                + "more > nul\r\n");
        }
        else
        {
            claude = Path.Combine(_folder, "claude");
            await File.WriteAllTextAsync(claude,
                $"#!/bin/sh\n(sleep 300 < /dev/null > /dev/null 2>&1 & echo $! > '{pidFile}.tmp' && mv '{pidFile}.tmp' '{pidFile}')\ncat > /dev/null\n");
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var group = ProcessGroupHelper.CreateProcessGroup();
        Process? server = null;
        try
        {
            var manager = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance);
            await manager.StartAsync(new ClaudeCodeProcessOptions
            {
                BinaryPath = claude,
                WorkingDirectory = _folder,
                PermissionMode = "bypassPermissions",
                ProcessGroup = group,
            }, CancellationToken.None);
            for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
                await Task.Delay(100);
            server = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture));

            // An idle stop: claude and what still runs under it end; the server doesn't.
            await manager.DisposeAsync();
            await Task.Delay(500);
            server.HasExited.ShouldBeFalse();

            // The session ends: on Windows its group takes the server with it. Linux and macOS have no group.
            group?.Dispose();
            if (OperatingSystem.IsWindows())
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            group?.Dispose();
            if (server is { HasExited: false })
                server.Kill();
            server?.Dispose();
        }
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
